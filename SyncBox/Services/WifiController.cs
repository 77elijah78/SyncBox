using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SyncBox.Services
{
    public sealed class WifiScanResult
    {
        public WifiScanResult(bool available, string summary, IReadOnlyList<WifiNetwork> networks)
        {
            Available = available;
            Summary = summary;
            Networks = networks;
        }

        /// <summary>True when the Windows Wi-Fi stack answered, even if nothing is in range.</summary>
        public bool Available { get; }

        public string Summary { get; }

        public IReadOnlyList<WifiNetwork> Networks { get; }
    }

    public sealed class WifiJoinOutcome
    {
        private WifiJoinOutcome(
            bool joined,
            string summary,
            string? controllerAddress,
            string? ssid,
            IReadOnlyList<WifiNetwork> networks)
        {
            Joined = joined;
            Summary = summary;
            ControllerAddress = controllerAddress;
            Ssid = ssid;
            Networks = networks;
        }

        public bool Joined { get; }

        public string Summary { get; }

        public string? ControllerAddress { get; }

        public string? Ssid { get; }

        public IReadOnlyList<WifiNetwork> Networks { get; }

        public static WifiJoinOutcome Connected(
            WifiNetwork network,
            string? controllerAddress,
            string summary,
            IReadOnlyList<WifiNetwork> networks) =>
            new(true, summary, controllerAddress, network.Ssid, networks);

        public static WifiJoinOutcome Failed(string summary, IReadOnlyList<WifiNetwork> networks) =>
            new(false, summary, null, null, networks);
    }

    /// <summary>
    /// Finds the controller's access point among the Wi-Fi networks in range and joins
    /// it, mirroring what <see cref="DeviceDiscovery"/> does for serial ports.
    /// </summary>
    public static class WifiController
    {
        private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>Scans every wireless interface and reports what is in range.</summary>
        public static WifiScanResult Scan(IProgress<string>? progress = null, CancellationToken token = default)
        {
            if (!IsSupported)
            {
                return new WifiScanResult(false, "Wi-Fi scanning is only available on Windows.", Array.Empty<WifiNetwork>());
            }

            try
            {
                using var session = WlanSession.Open();
                if (session.InterfaceCount == 0)
                {
                    return new WifiScanResult(false, "No wireless network adapter was found.", Array.Empty<WifiNetwork>());
                }

                progress?.Report("Scanning the Wi-Fi networks in range...");
                session.RequestScan();
                // Waiting for "any network" returned the stale cache: WlanScan takes a
                // few seconds and the list still holds the previous scan, so the
                // controller's own AP could be missing on the first try.
                IReadOnlyList<WifiNetwork> networks = session.WaitForNetworks(
                    ScanBudget,
                    list => WifiDiscovery.FindControllerNetworks(list).Count > 0,
                    token);

                return new WifiScanResult(true, WifiDiscovery.DescribeScan(networks), networks);
            }
            catch (OperationCanceledException)
            {
                return new WifiScanResult(true, "The Wi-Fi scan was cancelled or timed out.", Array.Empty<WifiNetwork>());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                return new WifiScanResult(false, "The Windows Wi-Fi service is not available on this machine.", Array.Empty<WifiNetwork>());
            }
            catch (Exception ex)
            {
                return new WifiScanResult(false, "The Wi-Fi scan failed: " + ex.Message, Array.Empty<WifiNetwork>());
            }
        }

        /// <summary>
        /// Scans, picks the best SyncBox network (or the caller's selection when it is
        /// still in range) and joins it. When the join succeeds the controller address
        /// is the DHCP gateway handed out by the controller's access point.
        /// </summary>
        public static WifiJoinOutcome JoinController(
            string? preferredSsid,
            string? password,
            IProgress<string>? progress = null,
            CancellationToken token = default)
        {
            if (!IsSupported)
            {
                return WifiJoinOutcome.Failed(
                    "Automatic Wi-Fi joining is only available on Windows.",
                    Array.Empty<WifiNetwork>());
            }

            try
            {
                using var session = WlanSession.Open();
                if (session.InterfaceCount == 0)
                {
                    return WifiJoinOutcome.Failed("No wireless network adapter was found.", Array.Empty<WifiNetwork>());
                }

                progress?.Report("Scanning the Wi-Fi networks in range...");
                session.RequestScan();

                IReadOnlyList<WifiNetwork> networks = session.WaitForNetworks(
                    ScanBudget,
                    list => WifiDiscovery.FindControllerNetworks(list).Count > 0,
                    token);

                IReadOnlyList<WifiNetwork> matches = WifiDiscovery.FindControllerNetworks(networks);
                if (matches.Count == 0)
                {
                    return WifiJoinOutcome.Failed(
                        WifiDiscovery.DescribeMatches(matches, networks.Count) +
                        " Power the controller on, or join its network once from the Windows Wi-Fi menu.",
                        networks);
                }

                WifiNetwork target = WifiDiscovery.ChooseControllerNetwork(matches, preferredSsid)!;
                if (target.IsConnected)
                {
                    string? currentAddress = session.FindGatewayAddress(target.InterfaceGuid);
                    string already = currentAddress is null
                        ? $"Already connected to {target.Ssid}."
                        : $"Already connected to {target.Ssid}. The controller is at {currentAddress}.";
                    return WifiJoinOutcome.Connected(target, currentAddress, already, networks);
                }

                progress?.Report($"Joining {target.Ssid}...");
                string? joinError = session.Connect(target, password);
                if (joinError is not null)
                {
                    return WifiJoinOutcome.Failed(joinError, networks);
                }

                progress?.Report($"Waiting for Windows to finish joining {target.Ssid}...");
                if (!session.WaitForAssociation(target, JoinTimeout, token))
                {
                    return WifiJoinOutcome.Failed(
                        $"Windows did not finish joining {target.Ssid} within {JoinTimeout.TotalSeconds:0} seconds. " +
                        "Check the Wi-Fi password and that the controller's access point is on.",
                        networks);
                }

                progress?.Report("Waiting for an IP address from the controller...");
                string? gateway = session.WaitForGateway(target.InterfaceGuid, AddressTimeout, token);
                string summary = gateway is null
                    ? $"Joined {target.Ssid}, but Windows has not received an IPv4 gateway yet."
                    : $"Joined {target.Ssid}. The controller is at {gateway}.";
                return WifiJoinOutcome.Connected(target, gateway, summary, networks);
            }
            catch (OperationCanceledException)
            {
                return WifiJoinOutcome.Failed("The Wi-Fi search was cancelled or timed out.", Array.Empty<WifiNetwork>());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                return WifiJoinOutcome.Failed(
                    "The Windows Wi-Fi service is not available on this machine.",
                    Array.Empty<WifiNetwork>());
            }
            catch (Exception ex)
            {
                return WifiJoinOutcome.Failed("Wi-Fi connection failed: " + ex.Message, Array.Empty<WifiNetwork>());
            }
        }

        private static string DecodeSsid(NativeWifi.WlanAvailableNetwork network)
        {
            byte[] bytes = network.ucSsid ?? Array.Empty<byte>();
            int length = (int)Math.Min(network.uSsidLength, (uint)bytes.Length);
            if (length <= 0)
            {
                return string.Empty;
            }

            string ssid = Encoding.UTF8.GetString(bytes, 0, length);
            return ssid.TrimEnd('\0');
        }

        private sealed class WlanInterface
        {
            public WlanInterface(Guid guid, string description)
            {
                Guid = guid;
                Description = description ?? string.Empty;
            }

            public Guid Guid { get; }

            public string Description { get; }
        }

        /// <summary>
        /// Owns one WLAN client handle for the duration of a scan or a join so the
        /// native handle and the unmanaged network lists are always released.
        /// </summary>
        private sealed class WlanSession : IDisposable
        {
            private readonly IntPtr _handle;
            private readonly List<WlanInterface> _interfaces;
            private bool _disposed;

            private WlanSession(IntPtr handle, List<WlanInterface> interfaces)
            {
                _handle = handle;
                _interfaces = interfaces;
            }

            public int InterfaceCount => _interfaces.Count;

            private static bool IsVirtualInterface(string? description) =>
                !string.IsNullOrWhiteSpace(description) &&
                (description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                 description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase) ||
                 description.Contains("Virtual Adapter", StringComparison.OrdinalIgnoreCase));

            public static WlanSession Open()
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException();
                }

                uint result = NativeWifi.WlanOpenHandle(
                    NativeWifi.ClientVersion,
                    IntPtr.Zero,
                    out _,
                    out IntPtr handle);
                if (result != 0)
                {
                    throw new InvalidOperationException(
                        "The Windows Wi-Fi service could not be opened: " + NativeWifi.Describe(result) + ".");
                }

                IntPtr buffer = IntPtr.Zero;
                try
                {
                    result = NativeWifi.WlanEnumInterfaces(handle, IntPtr.Zero, out buffer);
                    if (result != 0)
                    {
                        throw new InvalidOperationException(
                            "The wireless interfaces could not be listed: " + NativeWifi.Describe(result) + ".");
                    }

                    var interfaces = new List<WlanInterface>();
                    var physical = new List<WlanInterface>();
                    int count = Marshal.ReadInt32(buffer);
                    int size = Marshal.SizeOf<NativeWifi.WlanInterfaceInfo>();
                    for (int index = 0; index < count; index++)
                    {
                        IntPtr item = IntPtr.Add(buffer, NativeWifi.ListHeaderSize + (index * size));
                        NativeWifi.WlanInterfaceInfo info =
                            Marshal.PtrToStructure<NativeWifi.WlanInterfaceInfo>(item);
                        var wirelessInterface = new WlanInterface(info.InterfaceGuid, info.Description ?? string.Empty);
                        interfaces.Add(wirelessInterface);
                        if (!IsVirtualInterface(wirelessInterface.Description))
                        {
                            physical.Add(wirelessInterface);
                        }
                    }

                    // Hosted-network and Wi-Fi Direct adapters cannot associate with an
                    // access point, so prefer real radios and only fall back to the full
                    // list when every adapter looks virtual.
                    return new WlanSession(handle, physical.Count > 0 ? physical : interfaces);
                }
                catch
                {
                    NativeWifi.WlanCloseHandle(handle, IntPtr.Zero);
                    throw;
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        NativeWifi.WlanFreeMemory(buffer);
                    }
                }
            }

            public void RequestScan()
            {
                foreach (WlanInterface wirelessInterface in _interfaces)
                {
                    Guid guid = wirelessInterface.Guid;
                    NativeWifi.WlanScan(_handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
            }

            public IReadOnlyList<WifiNetwork> WaitForNetworks(
                TimeSpan budget,
                Func<IReadOnlyList<WifiNetwork>, bool> ready,
                CancellationToken token)
            {
                DateTime deadline = DateTime.UtcNow + budget;
                IReadOnlyList<WifiNetwork> networks = GetNetworks();

                while (!ready(networks) && DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    if (token.WaitHandle.WaitOne(PollInterval))
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    networks = GetNetworks();
                }

                return networks;
            }

            public string? Connect(WifiNetwork network, string? password)
            {
                string profile;
                int mode;

                // A stored Windows profile already holds the key, so reuse it unless the
                // user typed a password, which should win over whatever was saved before.
                if (network.HasProfile && string.IsNullOrEmpty(password))
                {
                    profile = network.ProfileName;
                    mode = NativeWifi.ConnectionModeProfile;
                }
                else
                {
                    string? xml = WifiProfileBuilder.Build(network, password, out string? profileError);
                    if (xml is null)
                    {
                        return profileError;
                    }

                    profile = xml;
                    mode = NativeWifi.ConnectionModeTemporaryProfile;
                }

                Guid guid = network.InterfaceGuid;
                var parameters = new NativeWifi.WlanConnectionParameters
                {
                    ConnectionMode = mode,
                    Profile = profile,
                    Dot11Ssid = IntPtr.Zero,
                    DesiredBssidList = IntPtr.Zero,
                    BssType = NativeWifi.BssTypeInfrastructure,
                    Flags = 0
                };

                uint result = NativeWifi.WlanConnect(_handle, ref guid, ref parameters, IntPtr.Zero);
                return result == 0
                    ? null
                    : $"Windows could not join {network.Ssid}: {NativeWifi.Describe(result)}.";
            }

            public bool WaitForAssociation(WifiNetwork target, TimeSpan timeout, CancellationToken token)
            {
                DateTime deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    bool connected = GetNetworks().Any(network =>
                        network.IsConnected &&
                        string.Equals(network.Ssid, target.Ssid, StringComparison.Ordinal));
                    if (connected)
                    {
                        return true;
                    }

                    if (token.WaitHandle.WaitOne(PollInterval))
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                return false;
            }

            public string? WaitForGateway(Guid interfaceGuid, TimeSpan timeout, CancellationToken token)
            {
                DateTime deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    string? address = FindGatewayAddress(interfaceGuid);
                    if (address is not null)
                    {
                        return address;
                    }

                    if (token.WaitHandle.WaitOne(PollInterval))
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                return null;
            }

            public string? FindGatewayAddress(Guid interfaceGuid)
            {
                WlanInterface? wirelessInterface = _interfaces.FirstOrDefault(
                    candidate => candidate.Guid == interfaceGuid);
                NetworkInterface? adapter = FindWirelessAdapter(wirelessInterface?.Description);

                if (adapter is null)
                {
                    adapter = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(candidate =>
                            candidate.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                            candidate.OperationalStatus == OperationalStatus.Up &&
                            !IsVirtualAdapter(candidate));
                }

                if (adapter is null)
                {
                    return null;
                }

                try
                {
                    foreach (GatewayIPAddressInformation gateway in adapter.GetIPProperties().GatewayAddresses)
                    {
                        IPAddress address = gateway.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
                        {
                            return address.ToString();
                        }
                    }
                }
                catch (NetworkInformationException)
                {
                }
                catch (PlatformNotSupportedException)
                {
                }

                return null;
            }

            private static NetworkInterface? FindWirelessAdapter(string? description)
            {
                if (string.IsNullOrWhiteSpace(description))
                {
                    return null;
                }

                try
                {
                    return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
                        candidate.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        string.Equals(candidate.Description, description.Trim(), StringComparison.OrdinalIgnoreCase));
                }
                catch (NetworkInformationException)
                {
                    return null;
                }
            }

            private static bool IsVirtualAdapter(NetworkInterface adapter) =>
                adapter.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("Direct", StringComparison.OrdinalIgnoreCase);

            private IReadOnlyList<WifiNetwork> GetNetworks()
            {
                var networks = new List<WifiNetwork>();
                int size = Marshal.SizeOf<NativeWifi.WlanAvailableNetwork>();

                foreach (WlanInterface wirelessInterface in _interfaces)
                {
                    Guid guid = wirelessInterface.Guid;
                    uint result = NativeWifi.WlanGetAvailableNetworkList(
                        _handle,
                        ref guid,
                        NativeWifi.AvailableNetworkListFlags,
                        IntPtr.Zero,
                        out IntPtr buffer);
                    if (result != 0 || buffer == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        int count = Marshal.ReadInt32(buffer);
                        for (int index = 0; index < count; index++)
                        {
                            IntPtr item = IntPtr.Add(buffer, NativeWifi.ListHeaderSize + (index * size));
                            var available = Marshal.PtrToStructure<NativeWifi.WlanAvailableNetwork>(item);
                            string ssid = DecodeSsid(available);
                            if (ssid.Length == 0)
                            {
                                continue;
                            }

                            bool secure = available.bSecurityEnabled != 0;
                            networks.Add(new WifiNetwork(
                                ssid,
                                (int)Math.Min(available.wlanSignalQuality, 100u),
                                secure,
                                NativeWifi.MapSecurity(
                                    secure,
                                    available.dot11DefaultAuthAlgorithm,
                                    available.dot11DefaultCipherAlgorithm),
                                (available.dwFlags & NativeWifi.AvailableNetworkConnectedFlag) != 0,
                                available.strProfileName ?? string.Empty,
                                guid));
                        }
                    }
                    finally
                    {
                        NativeWifi.WlanFreeMemory(buffer);
                    }
                }

                return networks;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                NativeWifi.WlanCloseHandle(_handle, IntPtr.Zero);
            }
        }
    }

    /// <summary>Minimal Native Wifi (wlanapi.dll) bindings used by <see cref="WifiController"/>.</summary>
    internal static class NativeWifi
    {
        public const uint ClientVersion = 2;

        public const int ListHeaderSize = 8;

        public const int ConnectionModeProfile = 0;

        public const int ConnectionModeTemporaryProfile = 1;

        public const int BssTypeInfrastructure = 1;

        public const uint AvailableNetworkConnectedFlag = 1;

        public const uint AvailableNetworkListFlags = 0;

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanOpenHandle(
            uint clientVersion,
            IntPtr reserved,
            out uint negotiatedVersion,
            out IntPtr clientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanEnumInterfaces(
            IntPtr clientHandle,
            IntPtr reserved,
            out IntPtr interfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanScan(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            IntPtr ssid,
            IntPtr informationElement,
            IntPtr reserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanGetAvailableNetworkList(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            uint flags,
            IntPtr reserved,
            out IntPtr availableNetworkList);

        [DllImport("wlanapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint WlanConnect(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            ref WlanConnectionParameters connectionParameters,
            IntPtr reserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern void WlanFreeMemory(IntPtr memory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;

            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WlanAvailableNetwork
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;

            public uint uSsidLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSsid;

            public int dot11BssType;

            public uint uNumberOfBssids;

            public int bNetworkConnectable;

            public uint wlanNotConnectableReason;

            public uint uNumberOfPhyTypes;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public int[] dot11PhyTypes;

            public int bMorePhyTypes;

            public uint wlanSignalQuality;

            public int bSecurityEnabled;

            public int dot11DefaultAuthAlgorithm;

            public int dot11DefaultCipherAlgorithm;

            public uint dwFlags;

            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WlanConnectionParameters
        {
            public int ConnectionMode;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Profile;

            public IntPtr Dot11Ssid;

            public IntPtr DesiredBssidList;

            public int BssType;

            public uint Flags;
        }

        public static WifiSecurityKind MapSecurity(bool isSecure, int authAlgorithm, int cipherAlgorithm)
        {
            if (!isSecure)
            {
                return WifiSecurityKind.Open;
            }

            // WEP (cipher 1, 5 and 0x101) and enterprise authentication are not
            // something SyncBox can reproduce from a single passphrase.
            if (cipherAlgorithm is 1 or 5 or 0x101)
            {
                return WifiSecurityKind.Unsupported;
            }

            return authAlgorithm switch
            {
                4 => WifiSecurityKind.WpaPsk,
                7 => WifiSecurityKind.Wpa2Psk,
                9 => WifiSecurityKind.Wpa3Sae,
                _ => WifiSecurityKind.Unsupported
            };
        }

        public static string Describe(uint code)
        {
            switch (code)
            {
                case 5:
                    return "access denied";
                case 87:
                    return "invalid parameter";
                case 1062:
                    return "the WLAN AutoConfig service is not running";
                case 1168:
                    return "the network was not found";
            }

            try
            {
                var exception = new Win32Exception((int)code);
                return string.IsNullOrWhiteSpace(exception.Message)
                    ? $"error {code}"
                    : $"{exception.Message} ({code})";
            }
            catch
            {
                return $"error {code}";
            }
        }
    }
}
