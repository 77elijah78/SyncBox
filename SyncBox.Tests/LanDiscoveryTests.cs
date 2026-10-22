using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using SyncBox.Services;
using Xunit;

namespace SyncBox.Tests
{
    public class LanDiscoveryTests
    {
        [Fact]
        public void EnumeratingHostsSweepsEveryAddressBetweenNetworkAndBroadcast()
        {
            IReadOnlyList<IPAddress> hosts = LanDiscovery.EnumerateHosts(
                IPAddress.Parse("192.168.1.37"),
                IPAddress.Parse("255.255.255.0"));

            Assert.Equal(254, hosts.Count);
            Assert.Equal("192.168.1.1", hosts[0].ToString());
            Assert.Equal("192.168.1.254", hosts[^1].ToString());
        }

        [Fact]
        public void LargeSubnetsAreCappedSoTheSweepStaysFast()
        {
            IReadOnlyList<IPAddress> hosts = LanDiscovery.EnumerateHosts(
                IPAddress.Parse("10.0.0.5"),
                IPAddress.Parse("255.255.0.0"),
                maxHosts: 300);

            Assert.Equal(300, hosts.Count);
            Assert.Equal("10.0.0.1", hosts[0].ToString());
        }

        [Fact]
        public void ASubnetKnowsItsBroadcastAddressForTheDiscoveryBurst()
        {
            var subnet = new LocalSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));

            Assert.Equal("192.168.1.255", subnet.Broadcast.ToString());
            Assert.True(subnet.Contains(subnet.Broadcast));
        }

        [Fact]
        public void ASubnetOnlyContainsItsOwnAddresses()
        {
            var subnet = new LocalSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));

            Assert.Equal("192.168.1.0/24", subnet.ToString());
            Assert.Equal(24, subnet.PrefixLength);
            Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.200")));
            Assert.False(subnet.Contains(IPAddress.Parse("192.168.2.10")));
            Assert.False(subnet.Contains(IPAddress.Parse("8.8.8.8")));
        }

        [Theory]
        [InlineData("SyncBox", 95)]
        [InlineData("Sync-Box-V1", 90)]
        [InlineData("sync box", 95)]
        [InlineData("MySyncBox", 85)]
        [InlineData("printer", 0)]
        [InlineData("BoxSync", 0)]
        [InlineData(null, 0)]
        public void HostNamesAreMatchedWithTheSameRulesAsNetworkNames(string? hostName, int expected)
        {
            Assert.Equal(expected, LanDiscovery.ScoreHostName(hostName));
        }

        [Theory]
        [InlineData("24:0A:C4:11:22:33", true)]
        [InlineData("24-0a-c4-11-22-33", true)]
        [InlineData("a4:cf:12:00:00:01", true)]
        [InlineData("F4:CF:A2:00:00:01", true)]
        [InlineData("00:11:22:33:44:55", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void EspressifHardwareIsRecognisedFromTheMacPrefix(string? mac, bool expected)
        {
            Assert.Equal(expected, LanDiscovery.IsEspressifOui(mac));
        }

        [Fact]
        public void VendorIsOnlyClaimedForKnownHardware()
        {
            Assert.Equal("Espressif", LanDiscovery.VendorFor("24:0A:C4:11:22:33"));
            Assert.Null(LanDiscovery.VendorFor("00:11:22:33:44:55"));
        }

        [Fact]
        public void TheHandshakeBeatsEveryOtherHint()
        {
            Assert.Equal(100, LanDiscovery.ScoreDevice("printer", "00:11:22:33:44:55", "Sync Box V1.0", false));
        }

        [Fact]
        public void ANameBuiltFromSyncBoxIsAStrongHint()
        {
            Assert.Equal(90, LanDiscovery.ScoreDevice("Sync-Box-V1", null, null, false));
            Assert.Equal(95, LanDiscovery.ScoreDevice("SyncBox", null, null, false));
        }

        [Fact]
        public void EspressifHardwareAndAnOpenLedPortAreWeakHints()
        {
            Assert.Equal(55, LanDiscovery.ScoreDevice(null, "24:0A:C4:11:22:33", null, false));
            Assert.Equal(60, LanDiscovery.ScoreDevice(null, null, null, true));
            Assert.Equal(10, LanDiscovery.ScoreDevice("printer", null, null, false));
        }

        [Fact]
        public void OnlyDevicesAboveTheThresholdArePickedAutomatically()
        {
            var devices = new List<LanDevice>
            {
                new(IPAddress.Parse("192.168.1.5"), "printer", null, null, null, false),
                new(IPAddress.Parse("192.168.1.9"), null, "24:0A:C4:11:22:33", "Espressif", null, false),
                new(IPAddress.Parse("192.168.1.42"), "Sync-Box-V1", "A4:CF:12:00:00:01", "Espressif", null, false),
            };

            LanDevice? chosen = LanDiscovery.ChooseControllerDevice(devices, null);

            Assert.NotNull(chosen);
            Assert.Equal("192.168.1.42", chosen!.AddressText);
        }

        [Fact]
        public void ANetworkOfOnlyPingableDevicesIsNotAssumedToBeTheController()
        {
            var devices = new List<LanDevice>
            {
                new(IPAddress.Parse("192.168.1.5"), "printer", "00:11:22:33:44:55", null, null, false),
                new(IPAddress.Parse("192.168.1.9"), null, "24:0A:C4:11:22:33", "Espressif", null, false),
            };

            Assert.Null(LanDiscovery.ChooseControllerDevice(devices, null));
            Assert.Null(LanDiscovery.ChooseControllerDevice(Array.Empty<LanDevice>(), "192.168.1.42"));
            Assert.Null(LanDiscovery.ChooseControllerDevice(null, null));
        }

        [Fact]
        public void APreviouslySelectedControllerWinsWhileItStillLooksLikeOne()
        {
            var devices = new List<LanDevice>
            {
                new(IPAddress.Parse("192.168.1.42"), "Sync-Box-V1", null, null, null, false),
                new(IPAddress.Parse("192.168.1.77"), null, null, null, "Sync Box V1.0", false),
            };

            LanDevice? chosen = LanDiscovery.ChooseControllerDevice(devices, "192.168.1.42");

            Assert.Equal("192.168.1.42", chosen!.AddressText);
        }

        [Fact]
        public void AStaleSelectionFallsBackToTheBestDevice()
        {
            var devices = new List<LanDevice>
            {
                new(IPAddress.Parse("192.168.1.42"), null, null, null, "Sync Box V1.0", false),
            };

            LanDevice? chosen = LanDiscovery.ChooseControllerDevice(devices, "192.168.1.200");

            Assert.Equal("192.168.1.42", chosen!.AddressText);
        }

        [Fact]
        public void DeviceLabelsShowAddressThenANameAndVendor()
        {
            var named = new LanDevice(
                IPAddress.Parse("192.168.1.42"),
                "Sync-Box-V1",
                "A4:CF:12:00:00:01",
                "Espressif",
                null,
                false);
            Assert.Equal("192.168.1.42 - Sync-Box-V1 (Espressif)", named.DisplayName);

            var handshake = new LanDevice(
                IPAddress.Parse("192.168.1.43"),
                "esp-1234",
                null,
                null,
                "Sync Box V1.0",
                false);
            Assert.Equal("192.168.1.43 - Sync Box V1.0", handshake.DisplayName);

            var anonymous = new LanDevice(
                IPAddress.Parse("192.168.1.44"),
                null,
                "24:0A:C4:11:22:33",
                "Espressif",
                null,
                false);
            Assert.Equal("192.168.1.44 - Espressif", anonymous.DisplayName);
        }

        [Fact]
        public void TheScanSummaryNamesTheLikelyController()
        {
            string empty = LanDiscovery.DescribeScan(Array.Empty<LanDevice>(), new[] { "192.168.1.0/24" });
            Assert.Contains("No devices found on 192.168.1.0/24", empty, StringComparison.Ordinal);

            var devices = new List<LanDevice>
            {
                new(IPAddress.Parse("192.168.1.42"), "Sync-Box-V1", null, null, null, false),
            };
            string found = LanDiscovery.DescribeScan(devices, new[] { "192.168.1.0/24" });
            Assert.Contains("Controller: 192.168.1.42 - Sync-Box-V1", found, StringComparison.Ordinal);

            var unclear = new List<LanDevice>
            {
                new(IPAddress.Parse("192.168.1.5"), "printer", null, null, null, false),
            };
            string unknown = LanDiscovery.DescribeScan(unclear, new[] { "192.168.1.0/24" });
            Assert.Contains("1 device(s) on 192.168.1.0/24", unknown, StringComparison.Ordinal);
            Assert.DoesNotContain("Controller:", unknown, StringComparison.Ordinal);
        }

        [Fact]
        public void OnlyDevicesThatIdentifyThemselvesAreListed()
        {
            var gateway = IPAddress.Parse("192.168.1.1");
            var gateways = new[] { gateway };

            var named = new LanDevice(IPAddress.Parse("192.168.1.42"), "Sync-Box-V1", null, null, null, false);
            var handshake = new LanDevice(IPAddress.Parse("192.168.1.43"), null, null, null, "Sync Box V1.0", false);
            var espressif = new LanDevice(IPAddress.Parse("192.168.1.44"), null, "24:0A:C4:11:22:33", "Espressif", null, false);
            var router = new LanDevice(gateway, "Router", "00:11:22:33:44:55", null, null, false);
            var bare = new LanDevice(IPAddress.Parse("192.168.1.99"), null, "AA:BB:CC:DD:EE:FF", null, null, false);

            Assert.True(LanDiscovery.IsIdentified(named, gateways));
            Assert.True(LanDiscovery.IsIdentified(handshake, gateways));
            Assert.True(LanDiscovery.IsIdentified(espressif, gateways));
            Assert.True(LanDiscovery.IsIdentified(router, gateways));
            Assert.False(LanDiscovery.IsIdentified(bare, gateways));
        }

        [Fact]
        public void GatewayAddressesComeBackWithoutDuplicates()
        {
            IReadOnlyCollection<IPAddress> gateways = LanDiscovery.FindGatewayAddresses();

            Assert.NotNull(gateways);
            Assert.Equal(gateways.Count, gateways.Distinct().Count());
        }

        [Fact]
        public void TheDiscoveryThresholdMatchesTheStrongHints()
        {
            Assert.Equal(80, LanDiscovery.ControllerScoreThreshold);
            Assert.True(LanDiscovery.ScoreDevice("Sync-Box-V1", null, null, false) >= LanDiscovery.ControllerScoreThreshold);
            Assert.False(LanDiscovery.ScoreDevice(null, "24:0A:C4:11:22:33", null, false) >= LanDiscovery.ControllerScoreThreshold);
        }
    }
}
