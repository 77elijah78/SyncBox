using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncBox.Services
{
    public sealed class LanDevice
    {
        public LanDevice(
            IPAddress address,
            string? hostName,
            string? macAddress,
            string? vendor,
            string? handshake,
            bool ledPortOpen)
        {
            Address = address;
            HostName = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Trim();
            MacAddress = string.IsNullOrWhiteSpace(macAddress) ? null : macAddress.Trim();
            Vendor = string.IsNullOrWhiteSpace(vendor) ? null : vendor;
            Handshake = string.IsNullOrWhiteSpace(handshake) ? null : handshake.Trim();
            LedPortOpen = ledPortOpen;
            Score = LanDiscovery.ScoreDevice(HostName, MacAddress, Handshake, ledPortOpen);
        }

        public IPAddress Address { get; }

        public string? HostName { get; }

        public string? MacAddress { get; }

        public string? Vendor { get; }

        public string? Handshake { get; }

        public bool LedPortOpen { get; }

        public int Score { get; }

        public string AddressText => Address.ToString();

        public bool LooksLikeController => Score >= LanDiscovery.ControllerScoreThreshold;

        /// <summary>"192.168.1.42 - Sync-Box-V1 (Espressif)" style label for the ComboBox.</summary>
        public string DisplayName
        {
            get
            {
                string label = Handshake ?? HostName ?? Vendor ?? "unnamed device";
                if (HostName is not null && Vendor is not null && !string.Equals(label, Vendor, StringComparison.Ordinal))
                {
                    label = $"{label} ({Vendor})";
                }

                return $"{AddressText} - {label}";
            }
        }

        public override string ToString() => DisplayName;
    }

    public sealed class LanScanResult
    {
        public LanScanResult(
            bool scanned,
            string summary,
            IReadOnlyList<LanDevice> devices,
            IReadOnlyList<string> subnets)
        {
            Scanned = scanned;
            Summary = summary;
            Devices = devices;
            Subnets = subnets;
        }

        /// <summary>True when at least one local IPv4 subnet was actually swept.</summary>
        public bool Scanned { get; }

        public string Summary { get; }

        public IReadOnlyList<LanDevice> Devices { get; }

        public IReadOnlyList<string> Subnets { get; }
    }

    /// <summary>
    /// Finds the controller as a device on the router the PC is connected to, instead
    /// of scanning Wi-Fi SSIDs. The PC's own IPv4 subnets are swept with ICMP, the ARP
    /// table fills in MAC addresses, and every live host is asked for its name and
    /// probed on the LED port with the same <c>AF 05 D1</c> handshake used over serial.
    /// </summary>
    public static class LanDiscovery
    {
        public const int MaxHostsPerSubnet = 1024;

        /// <summary>Score a device needs before Connect picks it without the user choosing.</summary>
        public const int ControllerScoreThreshold = 80;

        private const int ArpSettleMs = 600;
        private const int ReplyWindowMs = 400;
        private const int ResolveTimeoutMs = 200;
        private const int TcpProbeTimeoutMs = 300;
        private const int ProbeConcurrency = 48;
        private const int IdentifyBudgetMs = 2500;
        private const int MaxResolvedHosts = 512;

        private static readonly string[] VirtualAdapterMarkers =
        {
            "Virtual", "VMware", "VirtualBox", "Hyper-V", "vEthernet", "Loopback",
            "Pseudo", "Tailscale", "WireGuard", "OpenVPN", "TAP-", "WAN Miniport"
        };

        private static readonly string[] EspressifPrefixes =
        {
            "24:0A:C4", "24:62:AB", "2C:3A:E8", "30:AE:A4", "34:94:54", "3C:61:05",
            "3C:71:BF", "40:91:51", "44:17:93", "48:27:E2", "4C:11:AE", "54:5A:A6",
            "5C:CF:7F", "60:01:94", "68:C6:3A", "70:03:9F", "7C:9E:BD", "7C:DF:A1",
            "80:64:6F", "84:0D:8E", "84:CC:A8", "84:F3:EB", "8C:AA:B5", "90:38:0C",
            "94:B5:55", "94:B9:7E", "A0:20:A6", "A4:7B:9D", "A4:CF:12", "AC:67:B2",
            "B4:E6:2D", "B8:F0:09", "BC:DD:C2", "C4:4F:33", "C8:2B:96", "CC:50:E3",
            "D8:A0:1D", "D8:F1:5B", "DC:4F:22", "E0:98:06", "E8:68:E7", "E8:DB:84",
            "EC:FA:BC", "F0:08:D1", "F4:CF:A2", "FC:F5:C4"
        };

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>
        /// Finds the controller from the router's MAC/ARP table instead of pinging every
        /// address. Reading the table is instant, and a short burst of handshake packets
        /// makes the router fill it in for hosts the PC has not talked to yet.
        /// </summary>
        public static LanScanResult Scan(int port, IProgress<string>? progress = null, CancellationToken token = default)
        {
            if (!IsSupported)
            {
                return new LanScanResult(false, "Network scanning is only available on Windows.", Array.Empty<LanDevice>(), Array.Empty<string>());
            }

            try
            {
                IReadOnlyList<LocalSubnet> subnets = FindLocalSubnets();
                if (subnets.Count == 0)
                {
                    return new LanScanResult(
                        false,
                        "No IPv4 network connection was found. Connect the PC to the same router as the controller.",
                        Array.Empty<LanDevice>(),
                        Array.Empty<string>());
                }

                var subnetsText = subnets.Select(subnet => subnet.ToString()).ToList();

                // 1. Whatever the ARP table already knows costs nothing at all.
                IReadOnlyDictionary<IPAddress, string> arp = ReadArpTable();
                progress?.Report($"Reading the MAC table of {string.Join(", ", subnetsText)}...");

                // 2. One packet per address makes the router reveal the rest. Nothing is
                //    awaited, so this takes milliseconds instead of a ping timeout per host.
                Dictionary<IPAddress, string> replies = PrimeArpCache(subnets, port, token);

                // 3. Give the ARP table a moment to fill in, then read it again.
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(ArpSettleMs);
                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                {
                    arp = ReadArpTable();
                    bool foundEveryReply = replies.Keys.All(address => arp.ContainsKey(address));
                    if (arp.Count > 0 && foundEveryReply)
                    {
                        break;
                    }

                    token.WaitHandle.WaitOne(80);
                }

                arp = ReadArpTable();

                IReadOnlyCollection<IPAddress> gateways = FindGatewayAddresses();
                var alive = new HashSet<IPAddress>();
                foreach (KeyValuePair<IPAddress, string> entry in arp)
                {
                    if (subnets.Any(subnet => subnet.Contains(entry.Key)))
                    {
                        alive.Add(entry.Key);
                    }
                }

                foreach (IPAddress address in replies.Keys)
                {
                    alive.Add(address);
                }

                // Routers on guest and mobile networks answer ARP for every address, which
                // makes a whole subnet look "connected": those replies all carry the
                // gateway's MAC, so they are dropped before any name lookup happens.
                foreach (IPAddress gateway in gateways)
                {
                    if (!arp.TryGetValue(gateway, out string? gatewayMac))
                    {
                        continue;
                    }

                    alive.RemoveWhere(address =>
                        !address.Equals(gateway) &&
                        arp.TryGetValue(address, out string? mac) &&
                        string.Equals(mac, gatewayMac, StringComparison.OrdinalIgnoreCase));
                }

                if (alive.Count == 0)
                {
                    return new LanScanResult(
                        true,
                        $"No devices answered on {string.Join(", ", subnetsText)}. Check that the controller is powered on and on the same router.",
                        Array.Empty<LanDevice>(),
                        subnetsText);
                }

                progress?.Report($"Checking the devices on {string.Join(", ", subnetsText)}...");
                IReadOnlyList<LanDevice> devices = Identify(alive, arp, replies, gateways, port, token);

                // A router's client list only shows real devices: an address that has a
                // name, answers the handshake, is Espressif hardware or is the gateway.
                // Proxy-ARP noise (an address with a MAC and nothing else) stays hidden.
                IReadOnlyList<LanDevice> listed = devices
                    .Where(device => IsIdentified(device, gateways))
                    .ToList();

                string summary = DescribeScan(listed, subnetsText);
                return new LanScanResult(true, summary, listed, subnetsText);
            }
            catch (OperationCanceledException)
            {
                return new LanScanResult(false, "The network scan was cancelled or timed out.", Array.Empty<LanDevice>(), Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return new LanScanResult(false, "The network scan failed: " + ex.Message, Array.Empty<LanDevice>(), Array.Empty<string>());
            }
        }

        /// <summary>
        /// Sends the handshake to every address of the local subnets from one socket and
        /// collects the answers. Sending is fire-and-forget and also forces the operating
        /// system to resolve each MAC, which is what fills the ARP table.
        /// </summary>
        private static Dictionary<IPAddress, string> PrimeArpCache(
            IReadOnlyList<LocalSubnet> subnets,
            int port,
            CancellationToken token)
        {
            var replies = new Dictionary<IPAddress, string>();
            byte[] preamble = DeviceDiscovery.Preamble.ToArray();

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.EnableBroadcast = true;
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            catch (SocketException)
            {
                return replies;
            }

            foreach (LocalSubnet subnet in subnets)
            {
                token.ThrowIfCancellationRequested();
                foreach (IPAddress host in EnumerateHosts(subnet.Address, subnet.Mask))
                {
                    try
                    {
                        socket.SendTo(preamble, new IPEndPoint(host, port));
                    }
                    catch (SocketException)
                    {
                    }
                }

                if (port > 0)
                {
                    try
                    {
                        socket.SendTo(preamble, new IPEndPoint(subnet.Broadcast, port));
                    }
                    catch (SocketException)
                    {
                    }
                }
            }

            // Collect whatever came back without blocking the scan on silent devices.
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReplyWindowMs);
            var buffer = new byte[256];
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                if (!socket.Poll(20_000, SelectMode.SelectRead))
                {
                    continue;
                }

                try
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int read = socket.ReceiveFrom(buffer, ref remote);
                    if (read > 0 && remote is IPEndPoint endpoint)
                    {
                        // Only keep a reply that really is the firmware banner; anything
                        // else still proves the host exists but is not worth displaying.
                        string text = Encoding.ASCII.GetString(buffer, 0, read);
                        string? version = DeviceDiscovery.ExtractFirmwareVersion(text);
                        replies[endpoint.Address] = version is null ? string.Empty : text.Trim();
                    }
                }
                catch (SocketException)
                {
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            return replies;
        }

        public static IReadOnlyList<LocalSubnet> FindLocalSubnets()
        {
            var subnets = new List<LocalSubnet>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                        IsVirtualAdapter(adapter))
                    {
                        continue;
                    }

                    IPInterfaceProperties properties;
                    try
                    {
                        properties = adapter.GetIPProperties();
                    }
                    catch (NetworkInformationException)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        IPAddress address = unicast.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork || IsApipa(address))
                        {
                            continue;
                        }

                        IPAddress? mask;
                        try
                        {
                            // IPv4Mask is not supported for IPv6 entries on every platform.
                            mask = unicast.IPv4Mask;
                        }
                        catch (NotSupportedException)
                        {
                            continue;
                        }

                        if (mask is null || mask.Equals(IPAddress.Any))
                        {
                            continue;
                        }

                        subnets.Add(new LocalSubnet(address, mask));
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }

            return subnets
                .GroupBy(subnet => subnet.ToString(), StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(3)
                .ToList();
        }

        public static IReadOnlyList<IPAddress> EnumerateHosts(
            IPAddress address,
            IPAddress mask,
            int maxHosts = MaxHostsPerSubnet)
        {
            uint ip = ToUInt32(address);
            uint maskValue = ToUInt32(mask);
            uint network = ip & maskValue;
            uint broadcast = network | ~maskValue;
            uint hostCount = broadcast > network ? broadcast - network - 1 : 0;

            var hosts = new List<IPAddress>();
            uint limit = Math.Min(hostCount, (uint)Math.Max(1, maxHosts));
            for (uint offset = 1; offset <= limit; offset++)
            {
                hosts.Add(FromUInt32(network + offset));
            }

            return hosts;
        }

        /// <summary>Reuses the SSID rules, so "Sync-Box-V1" is a name match.</summary>
        public static int ScoreHostName(string? hostName)
        {
            int rank = WifiDiscovery.Rank(hostName);
            return rank switch
            {
                WifiDiscovery.ExactMatchRank => 95,
                WifiDiscovery.PrefixMatchRank => 90,
                WifiDiscovery.ContainsMatchRank => 85,
                _ => 0
            };
        }

        public static bool IsEspressifOui(string? macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress) || macAddress.Length < 8)
            {
                return false;
            }

            string prefix = macAddress.Trim().Replace('-', ':').ToUpperInvariant()[..8];
            return EspressifPrefixes.Contains(prefix, StringComparer.Ordinal);
        }

        public static string? VendorFor(string? macAddress) =>
            IsEspressifOui(macAddress) ? "Espressif" : null;

        public static int ScoreDevice(string? hostName, string? macAddress, string? handshake, bool ledPortOpen)
        {
            if (!string.IsNullOrWhiteSpace(handshake) &&
                handshake.Contains(DeviceDiscovery.FeedbackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            int best = ScoreHostName(hostName);
            if (IsEspressifOui(macAddress))
            {
                best = Math.Max(best, 55);
            }

            if (ledPortOpen)
            {
                best = Math.Max(best, 60);
            }

            return best > 0 ? best : 10;
        }

        /// <summary>
        /// Picks the controller: the caller's previous device wins while it still answers
        /// and looks like a controller, otherwise the best scored device above the
        /// threshold. Devices that only answered ping are never picked automatically.
        /// </summary>
        public static LanDevice? ChooseControllerDevice(IReadOnlyList<LanDevice>? devices, string? preferredAddress)
        {
            if (devices is null || devices.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(preferredAddress))
            {
                LanDevice? preferred = devices.FirstOrDefault(device =>
                    string.Equals(device.AddressText, preferredAddress.Trim(), StringComparison.OrdinalIgnoreCase));
                if (preferred is not null && preferred.LooksLikeController)
                {
                    return preferred;
                }
            }

            return devices
                .Where(device => device.LooksLikeController)
                .OrderByDescending(device => device.Score)
                .ThenBy(device => device.Address, IpAddressComparer.Instance)
                .FirstOrDefault();
        }

        public static string DescribeScan(IReadOnlyList<LanDevice> devices, IReadOnlyList<string> subnets)
        {
            string where = subnets.Count == 0 ? "the local network" : string.Join(", ", subnets);
            if (devices.Count == 0)
            {
                return $"No devices found on {where}.";
            }

            LanDevice? best = ChooseControllerDevice(devices, null);
            return best is null
                ? $"{devices.Count} device(s) on {where}."
                : $"{devices.Count} device(s) on {where}. Controller: {best.DisplayName}.";
        }

        /// <summary>
        /// True when the entry carries something a person can recognise, which is what a
        /// router's client list shows. Bare addresses are dropped.
        /// </summary>
        public static bool IsIdentified(LanDevice device, IReadOnlyCollection<IPAddress> gateways) =>
            device.Handshake is not null ||
            device.HostName is not null ||
            device.Vendor is not null ||
            gateways.Contains(device.Address);

        public static IReadOnlyCollection<IPAddress> FindGatewayAddresses()
        {
            var gateways = new HashSet<IPAddress>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (GatewayIPAddressInformation gateway in adapter.GetIPProperties().GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !gateway.Address.Equals(IPAddress.Any))
                        {
                            gateways.Add(gateway.Address);
                        }
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }

            return gateways;
        }

        private static IReadOnlyList<LanDevice> Identify(
            IReadOnlyCollection<IPAddress> addresses,
            IReadOnlyDictionary<IPAddress, string> arp,
            IReadOnlyDictionary<IPAddress, string> replies,
            IReadOnlyCollection<IPAddress> gateways,
            int port,
            CancellationToken token)
        {
            var devices = new List<LanDevice>();
            using var throttle = new SemaphoreSlim(ProbeConcurrency);

            // Name lookups are the slow part of the scan, so they run under their own
            // budget: whatever answered in time is kept, the rest is reported without a
            // name instead of holding the whole scan hostage.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(IdentifyBudgetMs);
            CancellationToken identifyToken = budget.Token;

            var tasks = new List<Task>();
            int candidates = 0;

            foreach (IPAddress address in addresses)
            {
                if (candidates++ >= MaxResolvedHosts)
                {
                    break;
                }

                token.ThrowIfCancellationRequested();
                tasks.Add(Task.Run(async () =>
                {
                    await throttle.WaitAsync(identifyToken).ConfigureAwait(false);
                    try
                    {
                        string? hostName = await ResolveHostNameAsync(address, identifyToken).ConfigureAwait(false);
                        if (hostName is null && gateways.Contains(address))
                        {
                            // The router rarely answers reverse DNS; give it a usable name.
                            hostName = "Router";
                        }
                        replies.TryGetValue(address, out string? handshake);
                        arp.TryGetValue(address, out string? mac);

                        // The handshake was already aimed at every address, so a silent
                        // host is silent; only Espressif hardware is worth a TCP probe.
                        bool portOpen = handshake is null &&
                                        port > 0 &&
                                        IsEspressifOui(mac) &&
                                        await ProbeTcpAsync(address, port, identifyToken).ConfigureAwait(false);

                        var device = new LanDevice(
                            address,
                            hostName,
                            mac,
                            VendorFor(mac),
                            handshake,
                            portOpen);

                        lock (devices)
                        {
                            devices.Add(device);
                        }
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }, token));
            }

            try
            {
                Task.WaitAll(tasks.ToArray(), identifyToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
                // A cancelled probe rejects with AggregateException; the devices that did
                // answer are still worth reporting.
            }

            IReadOnlyList<LanDevice> snapshot;
            lock (devices)
            {
                snapshot = devices.ToList();
            }

            return snapshot
                .OrderByDescending(device => device.Score)
                .ThenBy(device => device.Address, IpAddressComparer.Instance)
                .ToList();
        }

        private static async Task<string?> ResolveHostNameAsync(IPAddress address, CancellationToken token)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ResolveTimeoutMs);
                IPHostEntry entry = await Dns
                    .GetHostEntryAsync(address.ToString(), timeout.Token)
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
                string? name = entry.HostName;
                if (string.IsNullOrWhiteSpace(name) || name.Equals(address.ToString(), StringComparison.Ordinal))
                {
                    return null;
                }

                int dot = name.IndexOf('.');
                return dot > 0 ? name[..dot] : name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                return null;
            }
        }

        private static async Task<bool> ProbeTcpAsync(IPAddress address, int port, CancellationToken token)
        {
            using var client = new TcpClient();
            try
            {
                // TcpClient.ConnectAsync(..., CancellationToken) returns ValueTask, not
                // Task, so it has to be converted before it can race the timeout.
                Task connect = client.ConnectAsync(address, port, token).AsTask();
                Task finished = await Task
                    .WhenAny(connect, Task.Delay(TcpProbeTimeoutMs, token))
                    .ConfigureAwait(false);

                if (finished != connect)
                {
                    // The timeout won the race: observe the connect task so its eventual
                    // failure is not left unobserved when the client is disposed.
                    _ = connect.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return false;
                }

                await connect.ConfigureAwait(false);
                return client.Connected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                return false;
            }
        }

        private static IReadOnlyDictionary<IPAddress, string> ReadArpTable()
        {
            var table = new Dictionary<IPAddress, string>();
            if (!OperatingSystem.IsWindows())
            {
                return table;
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                int first = GetIpNetTable(IntPtr.Zero, ref size, true);
                if (size <= 0)
                {
                    return table;
                }

                buffer = Marshal.AllocHGlobal(size);
                int result = GetIpNetTable(buffer, ref size, true);
                if (result != 0)
                {
                    return table;
                }

                int rowSize = Marshal.SizeOf<MibIpNetRow>();
                int count = Marshal.ReadInt32(buffer);
                for (int index = 0; index < count; index++)
                {
                    IntPtr rowPointer = IntPtr.Add(buffer, 4 + (index * rowSize));
                    MibIpNetRow row = Marshal.PtrToStructure<MibIpNetRow>(rowPointer);
                    if (row.PhysAddrLen == 0 || row.PhysAddr is null)
                    {
                        continue;
                    }

                    var ip = new IPAddress((long)row.Addr);
                    if (IsApipa(ip) || ip.Equals(IPAddress.Any) || IsMulticast(ip))
                    {
                        continue;
                    }

                    int length = Math.Min((int)row.PhysAddrLen, row.PhysAddr.Length);
                    if (length == 0)
                    {
                        continue;
                    }

                    string mac = string.Join(":", row.PhysAddr.Take(length).Select(part => part.ToString("X2")));
                    table[ip] = mac;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return table;
        }

        private static bool IsVirtualAdapter(NetworkInterface adapter)
        {
            if (string.IsNullOrWhiteSpace(adapter.Description))
            {
                return false;
            }

            return VirtualAdapterMarkers.Any(marker =>
                adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsApipa(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
        }

        private static bool IsMulticast(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] >= 224;
        }

        private static uint ToUInt32(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return 0;
            }

            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        private static IPAddress FromUInt32(uint value) =>
            new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

        private sealed class IpAddressComparer : IComparer<IPAddress>
        {
            public static readonly IpAddressComparer Instance = new();

            public int Compare(IPAddress? left, IPAddress? right)
            {
                if (left is null || right is null)
                {
                    return left is null ? (right is null ? 0 : -1) : 1;
                }

                return ToUInt32(left).CompareTo(ToUInt32(right));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpNetRow
        {
            public int Index;

            public int PhysAddrLen;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[]? PhysAddr;

            public uint Addr;

            public int Type;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIpNetTable(IntPtr table, ref int size, bool sort);
    }

    public sealed class LocalSubnet
    {
        public LocalSubnet(IPAddress address, IPAddress mask)
        {
            Address = address;
            Mask = mask;
            PrefixLength = CountPrefixBits(mask);
            Network = AddressNetwork(address, mask);
            Broadcast = AddressBroadcast(Network, mask);
        }

        public IPAddress Address { get; }

        public IPAddress Mask { get; }

        public int PrefixLength { get; }

        public IPAddress Network { get; }

        public IPAddress Broadcast { get; }

        public bool Contains(IPAddress candidate)
        {
            byte[] networkBytes = Network.GetAddressBytes();
            byte[] maskBytes = Mask.GetAddressBytes();
            byte[] candidateBytes = candidate.GetAddressBytes();
            if (networkBytes.Length != 4 || candidateBytes.Length != 4)
            {
                return false;
            }

            for (int index = 0; index < 4; index++)
            {
                if ((networkBytes[index] & maskBytes[index]) != (candidateBytes[index] & maskBytes[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString() => $"{Network}/{PrefixLength}";

        private static int CountPrefixBits(IPAddress mask)
        {
            int bits = 0;
            foreach (byte part in mask.GetAddressBytes())
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    if ((part & (1 << bit)) != 0)
                    {
                        bits++;
                    }
                    else
                    {
                        return bits;
                    }
                }
            }

            return bits;
        }

        private static IPAddress AddressBroadcast(IPAddress network, IPAddress mask)
        {
            byte[] networkBytes = network.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();
            var broadcast = new byte[networkBytes.Length];
            for (int index = 0; index < networkBytes.Length; index++)
            {
                broadcast[index] = (byte)(networkBytes[index] | ~maskBytes[index]);
            }

            return new IPAddress(broadcast);
        }

        private static IPAddress AddressNetwork(IPAddress address, IPAddress mask)
        {
            byte[] addressBytes = address.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();
            var network = new byte[addressBytes.Length];
            for (int index = 0; index < addressBytes.Length; index++)
            {
                network[index] = (byte)(addressBytes[index] & maskBytes[index]);
            }

            return new IPAddress(network);
        }
    }
}
