using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using SyncBox.Services;
using Xunit;

namespace SyncBox.Tests
{
    public class WifiDiscoveryTests
    {
        [Theory]
        [InlineData("SyncBox", "syncbox")]
        [InlineData("  Sync Box  ", "syncbox")]
        [InlineData("SYNC_BOX-2G", "syncbox2g")]
        [InlineData("my.sync-box.lan", "mysyncboxlan")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void SsidNormalisationDropsSeparatorsAndCase(string? ssid, string expected)
        {
            Assert.Equal(expected, WifiDiscovery.NormalizeSsid(ssid));
        }

        [Theory]
        [InlineData("SyncBox", WifiDiscovery.ExactMatchRank)]
        [InlineData("sync box", WifiDiscovery.ExactMatchRank)]
        [InlineData("SyncBox-2G", WifiDiscovery.PrefixMatchRank)]
        [InlineData("SyncBox_5G", WifiDiscovery.PrefixMatchRank)]
        [InlineData("MySyncBox", WifiDiscovery.ContainsMatchRank)]
        [InlineData("Neighbour Wi-Fi", 0)]
        [InlineData("BoxSync", 0)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void OnlyNamesBuiltFromTheApplicationNameMatch(string? ssid, int expectedRank)
        {
            Assert.Equal(expectedRank, WifiDiscovery.Rank(ssid));
            Assert.Equal(expectedRank > 0, WifiDiscovery.IsControllerSsid(ssid));
        }

        [Fact]
        public void NetworksAreOrderedByMatchQualityThenConnectionThenSignal()
        {
            var networks = new List<WifiNetwork>
            {
                new("MySyncBox", 90, true, WifiSecurityKind.Wpa2Psk),
                new("SyncBox-2G", 70, true, WifiSecurityKind.Wpa2Psk),
                new("SyncBox", 20, false, WifiSecurityKind.Open),
                new("SyncBox", 80, false, WifiSecurityKind.Open, isConnected: true),
                new("Home Wi-Fi", 100, true, WifiSecurityKind.Wpa2Psk),
            };

            IReadOnlyList<WifiNetwork> matches = WifiDiscovery.FindControllerNetworks(networks);

            Assert.Equal(4, matches.Count);
            Assert.Equal(80, matches[0].SignalQuality);
            Assert.True(matches[0].IsConnected);
            Assert.Equal("SyncBox-2G", matches[2].Ssid);
            Assert.Equal("MySyncBox", matches[3].Ssid);
            Assert.DoesNotContain(matches, network => network.Ssid == "Home Wi-Fi");
        }

        [Fact]
        public void NullAndEmptyListsProduceNoMatches()
        {
            Assert.Empty(WifiDiscovery.FindControllerNetworks(null));
            Assert.Empty(WifiDiscovery.FindControllerNetworks(Array.Empty<WifiNetwork>()));
            Assert.Null(WifiDiscovery.ChooseControllerNetwork(null, "SyncBox"));
            Assert.Null(WifiDiscovery.ChooseControllerNetwork(Array.Empty<WifiNetwork>(), null));
        }

        [Fact]
        public void TheSelectedNetworkWinsWhenItIsStillInRange()
        {
            var matches = new List<WifiNetwork>
            {
                new("SyncBox", 95, false, WifiSecurityKind.Open),
                new("SyncBox-2G", 40, true, WifiSecurityKind.Wpa2Psk),
            };

            WifiNetwork? chosen = WifiDiscovery.ChooseControllerNetwork(matches, "syncbox-2g");

            Assert.NotNull(chosen);
            Assert.Equal("SyncBox-2G", chosen!.Ssid);
        }

        [Fact]
        public void AStaleSelectionFallsBackToTheStrongestMatch()
        {
            var matches = new List<WifiNetwork>
            {
                new("SyncBox", 30, false, WifiSecurityKind.Open),
                new("SyncBox-2G", 88, true, WifiSecurityKind.Wpa2Psk),
            };

            WifiNetwork? chosen = WifiDiscovery.ChooseControllerNetwork(matches, "Old SyncBox");

            Assert.NotNull(chosen);
            Assert.Equal("SyncBox-2G", chosen!.Ssid);
        }

        [Fact]
        public void ScanSummariesExplainWhatWasFound()
        {
            string none = WifiDiscovery.DescribeMatches(Array.Empty<WifiNetwork>(), 0);
            Assert.Contains("No Wi-Fi networks are in range", none, StringComparison.Ordinal);

            string others = WifiDiscovery.DescribeMatches(Array.Empty<WifiNetwork>(), 3);
            Assert.Contains("3 other network(s) visible", others, StringComparison.Ordinal);

            string one = WifiDiscovery.DescribeMatches(
                new[] { new WifiNetwork("SyncBox", 72, false, WifiSecurityKind.Open) },
                5);
            Assert.Contains("SyncBox (72%)", one, StringComparison.Ordinal);
        }

        [Fact]
        public void DisplayNameShowsStrengthAndSecurity()
        {
            Assert.Equal("SyncBox - 88% - connected",
                new WifiNetwork("SyncBox", 88, true, WifiSecurityKind.Wpa2Psk, isConnected: true).DisplayName);
            Assert.Equal("SyncBox - 88% - secured",
                new WifiNetwork("SyncBox", 88, true, WifiSecurityKind.Wpa2Psk).DisplayName);
            Assert.Equal("SyncBox - 88%",
                new WifiNetwork("SyncBox", 88, false, WifiSecurityKind.Open).DisplayName);
        }

        [Fact]
        public void SignalQualityIsClampedIntoThePercentageRange()
        {
            Assert.Equal(0, new WifiNetwork("SyncBox", -20, false).SignalQuality);
            Assert.Equal(100, new WifiNetwork("SyncBox", 420, false).SignalQuality);
        }
    }

    public class WifiSelectionTests
    {
        [Fact]
        public void EveryVisibleNetworkIsListedWithSyncBoxOnesFirst()
        {
            var networks = new List<WifiNetwork>
            {
                new("Neighbour", 99, true, WifiSecurityKind.Wpa2Psk),
                new("MySyncBox", 20, false, WifiSecurityKind.Open),
                new("SyncBox", 10, false, WifiSecurityKind.Open),
                new("Cafe", 80, true, WifiSecurityKind.Wpa2Psk),
            };

            IReadOnlyList<WifiNetwork> ordered = WifiDiscovery.SortForDisplay(networks);

            Assert.Equal(4, ordered.Count);
            Assert.Equal("SyncBox", ordered[0].Ssid);
            Assert.Equal("MySyncBox", ordered[1].Ssid);
            Assert.Equal("Neighbour", ordered[2].Ssid);
            Assert.Equal("Cafe", ordered[3].Ssid);
            Assert.Empty(WifiDiscovery.SortForDisplay(null));
        }

        [Fact]
        public void ANetworkWithADifferentNameCanStillBeSelectedByHand()
        {
            var networks = new List<WifiNetwork>
            {
                new("SyncBox", 90, false, WifiSecurityKind.Open),
                new("ESP_4F2A19", 40, false, WifiSecurityKind.Open),
            };

            WifiNetwork? chosen = WifiDiscovery.ChooseTarget(networks, "ESP_4F2A19");

            Assert.NotNull(chosen);
            Assert.Equal("ESP_4F2A19", chosen!.Ssid);
        }

        [Fact]
        public void WithoutASelectionTheBestSyncBoxNetworkIsUsed()
        {
            var networks = new List<WifiNetwork>
            {
                new("Neighbour", 99, true, WifiSecurityKind.Wpa2Psk),
                new("Sync-Box-V1", 55, false, WifiSecurityKind.Open),
            };

            WifiNetwork? chosen = WifiDiscovery.ChooseTarget(networks, null);

            Assert.NotNull(chosen);
            Assert.Equal("Sync-Box-V1", chosen!.Ssid);
        }

        [Fact]
        public void NoSelectionAndNoMatchYieldsNothing()
        {
            var networks = new List<WifiNetwork>
            {
                new("Neighbour", 99, true, WifiSecurityKind.Wpa2Psk),
            };

            Assert.Null(WifiDiscovery.ChooseTarget(networks, null));
            Assert.Null(WifiDiscovery.ChooseTarget(null, "SyncBox"));
            Assert.Null(WifiDiscovery.ChooseTarget(Array.Empty<WifiNetwork>(), "SyncBox"));
        }

        [Theory]
        [InlineData("Sync-Box-V1", true)]
        [InlineData("SyncBox", true)]
        [InlineData("Neighbour", false)]
        public void NetworksReportWhetherTheyLookLikeTheController(string ssid, bool expected)
        {
            Assert.Equal(expected, new WifiNetwork(ssid, 50, false, WifiSecurityKind.Open).LooksLikeController);
        }
    }

    public class WifiProfileTests
    {
        [Fact]
        public void AnOpenNetworkNeedsNoPassword()
        {
            string? xml = WifiProfileBuilder.Build(
                new WifiNetwork("SyncBox", 90, false, WifiSecurityKind.Open),
                null,
                out string? error);

            Assert.Null(error);
            Assert.NotNull(xml);
            XDocument document = XDocument.Parse(xml!);
            Assert.Equal("open", Element(document, "authentication"));
            Assert.Equal("none", Element(document, "encryption"));
            Assert.Null(Find(document, "sharedKey"));
        }

        [Theory]
        [InlineData(WifiSecurityKind.WpaPsk, "WPAPSK")]
        [InlineData(WifiSecurityKind.Wpa2Psk, "WPA2PSK")]
        [InlineData(WifiSecurityKind.Wpa3Sae, "WPA3SAE")]
        public void SecuredNetworksUsePassphraseProfiles(WifiSecurityKind kind, string expectedAuthentication)
        {
            string? xml = WifiProfileBuilder.Build(
                new WifiNetwork("SyncBox", 90, true, kind),
                "correct horse battery",
                out string? error);

            Assert.Null(error);
            XDocument document = XDocument.Parse(xml!);
            Assert.Equal(expectedAuthentication, Element(document, "authentication"));
            Assert.Equal("AES", Element(document, "encryption"));
            Assert.Equal("passPhrase", Element(document, "keyType"));
            Assert.Equal("correct horse battery", Element(document, "keyMaterial"));
            Assert.Equal("SyncBox", Element(document, "name"));
            Assert.Equal("ESS", Element(document, "connectionType"));
        }

        [Fact]
        public void ASecuredNetworkWithoutAPasswordIsRejectedWithGuidance()
        {
            string? xml = WifiProfileBuilder.Build(
                new WifiNetwork("SyncBox", 90, true, WifiSecurityKind.Wpa2Psk),
                null,
                out string? error);

            Assert.Null(xml);
            Assert.NotNull(error);
            Assert.Contains("password", error!, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("short")]
        [InlineData("1234567")]
        public void PassphrasesOutsideTheWpaRangeAreRejected(string password)
        {
            Assert.False(WifiProfileBuilder.IsValidPassphrase(password));

            string? xml = WifiProfileBuilder.Build(
                new WifiNetwork("SyncBox", 90, true, WifiSecurityKind.Wpa2Psk),
                password,
                out string? error);

            Assert.Null(xml);
            Assert.Contains("8 to 63", error!, StringComparison.Ordinal);
        }

        [Fact]
        public void PassphrasesOfTheMaximumLengthAreAccepted()
        {
            Assert.True(WifiProfileBuilder.IsValidPassphrase(new string('k', 63)));
            Assert.False(WifiProfileBuilder.IsValidPassphrase(new string('k', 64)));
        }

        [Fact]
        public void UnsupportedSecurityIsReportedInsteadOfGuessing()
        {
            string? xml = WifiProfileBuilder.Build(
                new WifiNetwork("SyncBox", 90, true, WifiSecurityKind.Unsupported),
                "correct horse battery",
                out string? error);

            Assert.Null(xml);
            Assert.Contains("cannot join automatically", error!, StringComparison.Ordinal);
        }

        [Fact]
        public void AnEmptySsidIsRejected()
        {
            string? xml = WifiProfileBuilder.Build("   ", false, WifiSecurityKind.Open, null, out string? error);
            Assert.Null(xml);
            Assert.NotNull(error);
        }

        private static string Element(XDocument document, string localName)
        {
            XElement? element = Find(document, localName);
            Assert.NotNull(element);
            return element!.Value;
        }

        private static XElement? Find(XDocument document, string localName)
        {
            XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
            return document.Root?
                .Descendants()
                .FirstOrDefault(node => node.Name == ns + localName);
        }
    }

    public class WifiSecurityMappingTests
    {
        [Theory]
        [InlineData(4, 2, WifiSecurityKind.WpaPsk)]
        [InlineData(7, 2, WifiSecurityKind.Wpa2Psk)]
        [InlineData(9, 2, WifiSecurityKind.Wpa3Sae)]
        [InlineData(3, 2, WifiSecurityKind.Unsupported)]
        [InlineData(7, 1, WifiSecurityKind.Unsupported)]
        [InlineData(7, 0x101, WifiSecurityKind.Unsupported)]
        public void AuthAndCipherAlgorithmsMapToWhatTheProfileBuilderSupports(
            int authAlgorithm,
            int cipherAlgorithm,
            WifiSecurityKind expected)
        {
            Assert.Equal(expected, NativeWifi.MapSecurity(true, authAlgorithm, cipherAlgorithm));
        }

        [Fact]
        public void AnUnsecuredNetworkIsAlwaysOpen()
        {
            Assert.Equal(WifiSecurityKind.Open, NativeWifi.MapSecurity(false, 7, 2));
        }
    }
}
