using System.Collections.Generic;
using System.Net;
using System.Text;
using SyncBox.Services;
using Xunit;

namespace SyncBox.Tests
{
    public class NetworkNamesTests
    {
        [Fact]
        public void AnMdnsAnswerKeepsTheInstanceNameAndDropsTheServiceType()
        {
            byte[] packet = MdnsPtr("_airplay._tcp.local", "Living Room TV._airplay._tcp.local");

            IReadOnlyList<string> names = NetworkNames.ReadMdnsNames(packet);

            Assert.Single(names);
            Assert.Equal("Living Room TV", names[0]);
        }

        [Fact]
        public void AnMdnsServiceRecordGivesTheHostName()
        {
            byte[] packet = MdnsSrv("_syncbox._tcp.local", "SyncBox-D4F1.local");

            IReadOnlyList<string> names = NetworkNames.ReadMdnsNames(packet);

            Assert.Single(names);
            Assert.Equal("SyncBox-D4F1", names[0]);
        }

        [Fact]
        public void ServiceDiscoveryAnswersNeverBecomeDeviceNames()
        {
            byte[] packet = MdnsPtr("_services._dns-sd._udp.local", "_http._tcp.local");

            Assert.Empty(NetworkNames.ReadMdnsNames(packet));
        }

        [Fact]
        public void ANetBiosAnswerYieldsTheWorkstationName()
        {
            byte[] packet = NetBios("DESKTOP-ABC");

            Assert.Equal("DESKTOP-ABC", NetworkNames.ReadNetBiosName(packet));
        }

        [Fact]
        public void ACutOffNetBiosPacketIsIgnored()
        {
            byte[] packet = NetBios("DESKTOP-ABC");

            Assert.Null(NetworkNames.ReadNetBiosName(packet[..40]));
            Assert.Null(NetworkNames.ReadNetBiosName(new byte[] { 0x00 }));
        }

        [Fact]
        public void AnSsdpReplyGivesTheAddressOfTheDevice()
        {
            const string response =
                "HTTP/1.1 200 OK\r\n" +
                "CACHE-CONTROL: max-age=120\r\n" +
                "LOCATION: http://192.168.1.50:49152/description.xml\r\n" +
                "SERVER: Linux/3.14 UPnP/1.0 IpBridge/1.30.0\r\n" +
                "ST: upnp:rootdevice\r\n\r\n";

            Assert.Equal(IPAddress.Parse("192.168.1.50"), NetworkNames.ReadSsdpHost(response));
            Assert.Equal("IpBridge", NetworkNames.ShortenServerHeader(NetworkNames.ReadSsdpHeader(response, "SERVER")));
            Assert.Null(NetworkNames.ReadSsdpHost("HTTP/1.1 200 OK\r\n\r\n"));
        }

        [Fact]
        public void TheFriendlyNameComesOutOfTheDeviceDescription()
        {
            const string xml =
                "<?xml version=\"1.0\"?><root><device>" +
                "<friendlyName>Living Room &amp; TV</friendlyName>" +
                "</device></root>";

            Assert.Equal("Living Room & TV", NetworkNames.ReadFriendlyName(xml));
            Assert.Null(NetworkNames.ReadFriendlyName("<root />"));
        }

        private static byte[] MdnsPtr(string service, string instance)
        {
            var bytes = new List<byte> { 0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
            WriteName(bytes, service);
            bytes.AddRange(new byte[] { 0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78 });

            var record = new List<byte>();
            WriteName(record, instance);
            bytes.Add((byte)(record.Count >> 8));
            bytes.Add((byte)(record.Count & 0xFF));
            bytes.AddRange(record);
            return bytes.ToArray();
        }

        private static byte[] MdnsSrv(string service, string target)
        {
            var bytes = new List<byte> { 0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
            WriteName(bytes, service);
            bytes.AddRange(new byte[] { 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78 });

            var record = new List<byte> { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            WriteName(record, target);
            bytes.Add((byte)(record.Count >> 8));
            bytes.Add((byte)(record.Count & 0xFF));
            bytes.AddRange(record);
            return bytes.ToArray();
        }

        private static byte[] NetBios(string name)
        {
            var bytes = new List<byte> { 0x82, 0x1B, 0x84, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };

            WriteEncodedNetBiosName(bytes);
            bytes.AddRange(new byte[] { 0x00, 0x21, 0x00, 0x01 });

            WriteEncodedNetBiosName(bytes);
            bytes.AddRange(new byte[] { 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });

            var record = new List<byte> { 0x01 };
            var padded = new byte[15];
            Encoding.ASCII.GetBytes(name).CopyTo(padded, 0);
            record.AddRange(padded);
            record.AddRange(new byte[] { 0x00, 0x00, 0x00 });
            bytes.Add((byte)(record.Count >> 8));
            bytes.Add((byte)(record.Count & 0xFF));
            bytes.AddRange(record);
            return bytes.ToArray();
        }

        private static void WriteEncodedNetBiosName(List<byte> bytes)
        {
            bytes.Add(0x20);
            bytes.AddRange(Encoding.ASCII.GetBytes("CK"));
            bytes.AddRange(Encoding.ASCII.GetBytes(new string('A', 30)));
            bytes.Add(0x00);
        }

        private static void WriteName(List<byte> bytes, string name)
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
    }
}
