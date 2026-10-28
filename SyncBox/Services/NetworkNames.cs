using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SyncBox.Services
{
    /// <summary>A name a device announced on the network, plus where it came from.</summary>
    public sealed class LanName
    {
        public LanName(string name, string source)
        {
            Name = name;
            Source = source;
        }

        public string Name { get; }

        public string Source { get; }
    }

    /// <summary>
    /// Asks the devices on the local network to introduce themselves. The ARP table only
    /// says which addresses have been talked to, and a router that answers ARP for the
    /// whole subnet hides every client from it; mDNS, SSDP and NetBIOS are answered by the
    /// devices themselves, which is what turns an address into "Living Room TV" or
    /// "DESKTOP-ABC".
    /// </summary>
    public static class NetworkNames
    {
        public const int DefaultWindowMs = 900;

        private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
        private static readonly IPAddress SsdpGroup = IPAddress.Parse("239.255.255.250");
        private const int MdnsPort = 5353;
        private const int SsdpPort = 1900;
        private const int NetBiosPort = 137;

        private static readonly string[] MdnsServices =
        {
            "_services._dns-sd._udp.local",
            "_http._tcp.local",
            "_workstation._tcp.local",
            "_airplay._tcp.local",
            "_raop._tcp.local",
            "_googlecast._tcp.local",
            "_spotify-connect._tcp.local",
            "_ipp._tcp.local",
            "_printer._tcp.local",
            "_smb._tcp.local",
            "_ssh._tcp.local",
            "_syncbox._tcp.local"
        };

        /// <summary>
        /// Collects every name the subnet is willing to hand out. Each protocol runs on its
        /// own socket and its own thread, so a blocked one never holds up the others.
        /// </summary>
        public static IReadOnlyDictionary<IPAddress, LanName> Discover(
            IReadOnlyList<LocalSubnet> subnets,
            int windowMs,
            CancellationToken token)
        {
            var names = new ConcurrentDictionary<IPAddress, LanName>();
            int window = Math.Clamp(windowMs, 250, 5000);

            var tasks = new List<Task>
            {
                Task.Run(() => CollectMdns(subnets, names, window, token)),
                Task.Run(() => CollectSsdp(names, window, token)),
                Task.Run(() => CollectNetBios(subnets, names, window, token))
            };

            try
            {
                Task.WaitAll(tasks.ToArray(), window + 2500);
            }
            catch (AggregateException)
            {
            }

            return names;
        }

        /// <summary>Builds one DNS query packet holding a PTR question per service type.</summary>
        public static byte[] BuildMdnsQuery()
        {
            var bytes = new List<byte>
            {
                0x00, 0x00, 0x00, 0x00,
                (byte)(MdnsServices.Length >> 8), (byte)(MdnsServices.Length & 0xFF),
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };

            foreach (string service in MdnsServices)
            {
                WriteDnsName(bytes, service);
                bytes.Add(0x00);
                bytes.Add(0x0C);
                bytes.Add(0x00);
                bytes.Add(0x01);
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// Pulls the device names out of an mDNS response: the PTR instance name
        /// ("Living Room TV._airplay._tcp.local") and the SRV target ("Living-Room-TV.local").
        /// Service type labels such as "_airplay" are skipped.
        /// </summary>
        public static IReadOnlyList<string> ReadMdnsNames(ReadOnlySpan<byte> packet)
        {
            var names = new List<string>();
            if (packet.Length < 12)
            {
                return names;
            }

            int questions = (packet[4] << 8) | packet[5];
            int answers = (packet[6] << 8) | packet[7];
            int authority = (packet[8] << 8) | packet[9];
            int additional = (packet[10] << 8) | packet[11];
            int offset = 12;

            for (int index = 0; index < questions; index++)
            {
                if (!TryReadDnsName(packet, offset, out _, out int questionEnd) || questionEnd + 4 > packet.Length)
                {
                    return names;
                }

                offset = questionEnd + 4;
            }

            int records = answers + authority + additional;
            for (int index = 0; index < records; index++)
            {
                if (!TryReadDnsName(packet, offset, out _, out int recordStart) || recordStart + 10 > packet.Length)
                {
                    break;
                }

                offset = recordStart;
                int type = (packet[offset] << 8) | packet[offset + 1];
                int length = (packet[offset + 8] << 8) | packet[offset + 9];
                int data = offset + 10;
                if (length < 0 || data + length > packet.Length)
                {
                    break;
                }

                if (type == 12)
                {
                    if (TryReadDnsName(packet, data, out string instance, out _))
                    {
                        AddName(names, instance);
                    }
                }
                else if (type == 33 && length > 6)
                {
                    if (TryReadDnsName(packet, data + 6, out string target, out _))
                    {
                        AddName(names, target);
                    }
                }

                offset = data + length;
            }

            return names;
        }

        /// <summary>Reads the NetBIOS workstation name out of an NBSTAT reply.</summary>
        public static string? ReadNetBiosName(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 12)
            {
                return null;
            }

            int questions = (packet[4] << 8) | packet[5];
            int answers = (packet[6] << 8) | packet[7];
            int offset = 12;

            for (int index = 0; index < questions; index++)
            {
                if (!TryReadDnsName(packet, offset, out _, out int questionEnd) || questionEnd + 4 > packet.Length)
                {
                    return null;
                }

                offset = questionEnd + 4;
            }

            if (answers <= 0 ||
                !TryReadDnsName(packet, offset, out _, out int answerStart) ||
                answerStart + 10 > packet.Length)
            {
                return null;
            }

            offset = answerStart;

            int length = (packet[offset + 8] << 8) | packet[offset + 9];
            int data = offset + 10;
            if (length < 1 || data + length > packet.Length)
            {
                return null;
            }

            int count = packet[data];
            for (int index = 0; index < count; index++)
            {
                int start = data + 1 + (index * 18);
                if (start + 18 > packet.Length)
                {
                    break;
                }

                string name = Encoding.ASCII.GetString(packet.Slice(start, 15)).Trim();
                byte suffix = packet[start + 15];
                bool group = (packet[start + 16] & 0x01) != 0;
                if (!group && name.Length > 0 && (suffix == 0x00 || suffix == 0x20))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>Builds the wildcard NBSTAT query that makes Windows hosts name themselves.</summary>
        public static byte[] BuildNetBiosQuery()
        {
            var bytes = new List<byte>
            {
                0x82, 0x1B, 0x00, 0x00,
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x20, 0x43, 0x4B
            };

            for (int index = 0; index < 30; index++)
            {
                bytes.Add(0x41);
            }

            bytes.Add(0x00);
            bytes.Add(0x00);
            bytes.Add(0x21);
            bytes.Add(0x00);
            bytes.Add(0x01);

            return bytes.ToArray();
        }

        /// <summary>Reads one header out of an SSDP reply.</summary>
        public static string? ReadSsdpHeader(string response, string header)
        {
            if (string.IsNullOrEmpty(response))
            {
                return null;
            }

            foreach (string line in response.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                if (line[..colon].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }

            return null;
        }

        /// <summary>Turns the LOCATION header of an SSDP reply into the device address.</summary>
        public static IPAddress? ReadSsdpHost(string response)
        {
            string? location = ReadSsdpHeader(response, "LOCATION");
            if (location is null)
            {
                return null;
            }

            return Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) &&
                   IPAddress.TryParse(uri.Host, out IPAddress? address)
                ? address
                : null;
        }

        /// <summary>"Linux/3.14 UPnP/1.0 IpBridge/1.30.0" becomes "IpBridge".</summary>
        public static string? ShortenServerHeader(string? server)
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            string[] parts = server.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                if (parts[index].StartsWith("UPnP", StringComparison.OrdinalIgnoreCase) && index + 1 < parts.Length)
                {
                    return ClipVersion(parts[index + 1]);
                }
            }

            return parts.Length > 0 ? ClipVersion(parts[^1]) : null;
        }

        /// <summary>Reads the &lt;friendlyName&gt; out of a UPnP device description.</summary>
        public static string? ReadFriendlyName(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            Match match = Regex.Match(
                xml,
                "<friendlyName>(.*?)</friendlyName>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            string name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            return name.Length is > 0 and <= 60 ? name : null;
        }

        private static void CollectMdns(
            IReadOnlyList<LocalSubnet> subnets,
            ConcurrentDictionary<IPAddress, LanName> names,
            int windowMs,
            CancellationToken token)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                foreach (LocalSubnet subnet in subnets)
                {
                    try
                    {
                        socket.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(MdnsGroup, subnet.Address));
                    }
                    catch (SocketException)
                    {
                    }
                }

                try
                {
                    socket.SendTo(BuildMdnsQuery(), new IPEndPoint(MdnsGroup, MdnsPort));
                }
                catch (SocketException)
                {
                    return;
                }

                var buffer = new byte[4096];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                {
                    if (!socket.Poll(25_000, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int read;
                    try
                    {
                        read = socket.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    if (read <= 12 || remote is not IPEndPoint endpoint)
                    {
                        continue;
                    }

                    foreach (string name in ReadMdnsNames(buffer.AsSpan(0, read)))
                    {
                        names.TryAdd(endpoint.Address, new LanName(name, "mDNS"));
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void CollectSsdp(
            ConcurrentDictionary<IPAddress, LanName> names,
            int windowMs,
            CancellationToken token)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                byte[] query = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 1\r\n" +
                    "ST: ssdp:all\r\n\r\n");

                try
                {
                    socket.SendTo(query, new IPEndPoint(SsdpGroup, SsdpPort));
                }
                catch (SocketException)
                {
                    return;
                }

                var locations = new Dictionary<IPAddress, string>();
                var buffer = new byte[4096];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                {
                    if (!socket.Poll(25_000, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int read;
                    try
                    {
                        read = socket.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    if (read <= 0 || remote is not IPEndPoint endpoint)
                    {
                        continue;
                    }

                    string text = Encoding.ASCII.GetString(buffer, 0, read);
                    if (!text.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? label = ShortenServerHeader(ReadSsdpHeader(text, "SERVER"));
                    if (label is not null)
                    {
                        names.TryAdd(endpoint.Address, new LanName(label, "UPnP"));
                    }

                    string? location = ReadSsdpHeader(text, "LOCATION");
                    if (location is not null && locations.Count < 8)
                    {
                        locations[endpoint.Address] = location;
                    }
                }

                FetchSsdpFriendlyNames(locations, names);
            }
            catch (Exception)
            {
            }
        }

        private static void FetchSsdpFriendlyNames(
            Dictionary<IPAddress, string> locations,
            ConcurrentDictionary<IPAddress, LanName> names)
        {
            if (locations.Count == 0)
            {
                return;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(450) };
                var tasks = locations.Select(async pair =>
                {
                    try
                    {
                        string xml = await client.GetStringAsync(pair.Value).ConfigureAwait(false);
                        string? friendly = ReadFriendlyName(xml);
                        if (friendly is not null)
                        {
                            names[pair.Key] = new LanName(friendly, "UPnP");
                        }
                    }
                    catch (Exception)
                    {
                    }
                }).ToArray();

                Task.WaitAll(tasks, 800);
            }
            catch (Exception)
            {
            }
        }

        private static void CollectNetBios(
            IReadOnlyList<LocalSubnet> subnets,
            ConcurrentDictionary<IPAddress, LanName> names,
            int windowMs,
            CancellationToken token)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                byte[] query = BuildNetBiosQuery();
                foreach (LocalSubnet subnet in subnets)
                {
                    try
                    {
                        socket.SendTo(query, new IPEndPoint(subnet.Broadcast, NetBiosPort));
                    }
                    catch (SocketException)
                    {
                    }
                }

                var buffer = new byte[2048];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                {
                    if (!socket.Poll(25_000, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int read;
                    try
                    {
                        read = socket.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    if (read <= 0 || remote is not IPEndPoint endpoint)
                    {
                        continue;
                    }

                    string? name = ReadNetBiosName(buffer.AsSpan(0, read));
                    if (name is not null)
                    {
                        names.TryAdd(endpoint.Address, new LanName(name, "NetBIOS"));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void AddName(List<string> names, string raw)
        {
            string? name = CleanName(raw);
            if (name is not null && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        private static string? CleanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string value = raw.Trim().TrimEnd('.');
            int service = value.IndexOf("._", StringComparison.Ordinal);
            if (service > 0)
            {
                value = value[..service];
            }

            if (value.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^6];
            }

            value = value.Trim();
            if (value.Length is 0 or > 63 ||
                value.StartsWith("_", StringComparison.Ordinal) ||
                value.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value;
        }

        private static bool TryReadDnsName(
            ReadOnlySpan<byte> packet,
            int offset,
            out string name,
            out int nextOffset)
        {
            name = string.Empty;
            nextOffset = offset;
            var builder = new StringBuilder();
            int jumps = 0;

            while (true)
            {
                if (offset < 0 || offset >= packet.Length)
                {
                    return false;
                }

                int length = packet[offset];
                if (length == 0)
                {
                    offset++;
                    if (jumps == 0)
                    {
                        nextOffset = offset;
                    }

                    name = builder.ToString();
                    return true;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (offset + 1 >= packet.Length)
                    {
                        return false;
                    }

                    int pointer = ((length & 0x3F) << 8) | packet[offset + 1];
                    if (jumps == 0)
                    {
                        nextOffset = offset + 2;
                    }

                    offset = pointer;
                    if (++jumps > 20)
                    {
                        return false;
                    }

                    continue;
                }

                offset++;
                if (offset + length > packet.Length || builder.Length > 200)
                {
                    return false;
                }

                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(Encoding.ASCII.GetString(packet.Slice(offset, length)));
                offset += length;
            }
        }

        private static void WriteDnsName(List<byte> bytes, string name)
        {
            foreach (string label in name.Split('.'))
            {
                if (label.Length == 0)
                {
                    continue;
                }

                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.ASCII.GetBytes(label));
            }

            bytes.Add(0x00);
        }

        private static string? ClipVersion(string token)
        {
            string value = token.Trim();
            int slash = value.IndexOf('/');
            if (slash > 0)
            {
                value = value[..slash];
            }

            value = value.Replace('_', ' ').Trim();
            return value.Length is > 0 and <= 40 ? value : null;
        }
    }
}
