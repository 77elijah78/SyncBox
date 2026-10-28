using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SyncBox.Services
{
    public enum WifiSecurityKind
    {
        Open = 0,
        WpaPsk,
        Wpa2Psk,
        Wpa3Sae,
        Unsupported
    }

    public sealed class WifiNetwork
    {
        public WifiNetwork(
            string ssid,
            int signalQuality,
            bool isSecure,
            WifiSecurityKind securityKind = WifiSecurityKind.Open,
            bool isConnected = false,
            string profileName = "",
            Guid interfaceGuid = default)
        {
            Ssid = ssid ?? string.Empty;
            SignalQuality = Math.Clamp(signalQuality, 0, 100);
            IsSecure = isSecure;
            SecurityKind = securityKind;
            IsConnected = isConnected;
            ProfileName = profileName ?? string.Empty;
            InterfaceGuid = interfaceGuid;
        }

        public string Ssid { get; }

        public int SignalQuality { get; }

        public bool IsSecure { get; }

        public WifiSecurityKind SecurityKind { get; }

        public bool IsConnected { get; }

        public string ProfileName { get; }

        public Guid InterfaceGuid { get; }

        public bool HasProfile => !string.IsNullOrWhiteSpace(ProfileName);

        public bool LooksLikeController => WifiDiscovery.IsControllerSsid(Ssid);

        public string DisplayName
        {
            get
            {
                string suffix = IsConnected ? " - connected" : IsSecure ? " - secured" : string.Empty;
                return $"{Ssid} - {SignalQuality}%{suffix}";
            }
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Pure matching rules used to decide which nearby Wi-Fi network belongs to the
    /// controller. The controller advertises an SSID built from the application name,
    /// for example "SyncBox", "Sync Box", "SyncBox-2G" or "MySyncBox".
    /// </summary>
    public static class WifiDiscovery
    {
        public const string ControllerName = "SyncBox";

        public const int ExactMatchRank = 3;

        public const int PrefixMatchRank = 2;

        public const int ContainsMatchRank = 1;

        /// <summary>
        /// Lower-cases the SSID and drops every separator (spaces, dashes, underscores,
        /// dots) so "Sync Box", "syncbox-2g" and "SYNC.BOX" all compare predictably.
        /// </summary>
        public static string NormalizeSsid(string? ssid)
        {
            if (string.IsNullOrWhiteSpace(ssid))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(ssid.Length);
            foreach (char character in ssid)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }

        public static int Rank(string? ssid)
        {
            string normalized = NormalizeSsid(ssid);
            if (normalized.Length == 0)
            {
                return 0;
            }

            string name = NormalizeSsid(ControllerName);
            if (normalized.Length == name.Length && normalized == name)
            {
                return ExactMatchRank;
            }

            if (normalized.StartsWith(name, StringComparison.Ordinal))
            {
                return PrefixMatchRank;
            }

            return normalized.Contains(name, StringComparison.Ordinal) ? ContainsMatchRank : 0;
        }

        public static bool IsControllerSsid(string? ssid) => Rank(ssid) > 0;

        /// <summary>
        /// Returns every nearby network that looks like a controller, best match first.
        /// </summary>
        public static IReadOnlyList<WifiNetwork> FindControllerNetworks(IEnumerable<WifiNetwork>? networks)
        {
            if (networks is null)
            {
                return Array.Empty<WifiNetwork>();
            }

            return networks
                .Where(network => Rank(network.Ssid) > 0)
                .OrderByDescending(network => Rank(network.Ssid))
                .ThenByDescending(network => network.IsConnected)
                .ThenByDescending(network => network.SignalQuality)
                .ToList();
        }

        /// <summary>
        /// Every visible network, with the SyncBox-looking ones first so the names that
        /// matter are readable at the top of the list.
        /// </summary>
        public static IReadOnlyList<WifiNetwork> SortForDisplay(IEnumerable<WifiNetwork>? networks)
        {
            if (networks is null)
            {
                return Array.Empty<WifiNetwork>();
            }

            return networks
                .OrderByDescending(network => Rank(network.Ssid))
                .ThenByDescending(network => network.IsConnected)
                .ThenByDescending(network => network.SignalQuality)
                .ThenBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Picks the network to join: the caller's selection when it is in range, else the
        /// best SyncBox match. Any network can be joined when it is selected by hand, so
        /// an access point with a different name still works.
        /// </summary>
        public static WifiNetwork? ChooseTarget(IReadOnlyList<WifiNetwork>? networks, string? preferredSsid)
        {
            if (networks is null || networks.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(preferredSsid))
            {
                string preferred = preferredSsid.Trim();
                WifiNetwork? selected = networks.FirstOrDefault(
                    network => string.Equals(network.Ssid, preferred, StringComparison.OrdinalIgnoreCase));
                if (selected is not null)
                {
                    return selected;
                }
            }

            return ChooseControllerNetwork(FindControllerNetworks(networks), null);
        }

        /// <summary>
        /// Picks the network to join. An explicit selection wins when it is still in
        /// range, otherwise the best ranked and strongest match is used.
        /// </summary>
        public static WifiNetwork? ChooseControllerNetwork(
            IReadOnlyList<WifiNetwork>? matches,
            string? preferredSsid)
        {
            if (matches is null || matches.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(preferredSsid))
            {
                string preferred = preferredSsid.Trim();
                WifiNetwork? selected = matches.FirstOrDefault(
                    network => string.Equals(network.Ssid, preferred, StringComparison.OrdinalIgnoreCase));
                if (selected is not null)
                {
                    return selected;
                }
            }

            WifiNetwork best = matches[0];
            for (int index = 1; index < matches.Count; index++)
            {
                if (Compare(matches[index], best) > 0)
                {
                    best = matches[index];
                }
            }

            return best;
        }

        public static string DescribeMatches(IReadOnlyList<WifiNetwork> matches, int visibleNetworkCount)
        {
            if (matches.Count == 0)
            {
                int others = Math.Max(0, visibleNetworkCount);
                return others == 0
                    ? "No Wi-Fi networks are in range."
                    : $"No SyncBox Wi-Fi network is in range ({others} other network(s) visible).";
            }

            string list = string.Join(", ", matches.Select(
                network => $"{network.Ssid} ({network.SignalQuality}%)"));
            return matches.Count == 1
                ? $"Found the SyncBox network {list}."
                : $"Found {matches.Count} SyncBox networks: {list}.";
        }

        public static string DescribeScan(IReadOnlyList<WifiNetwork> networks)
        {
            IReadOnlyList<WifiNetwork> matches = FindControllerNetworks(networks);
            return DescribeMatches(matches, networks.Count);
        }

        private static int Compare(WifiNetwork left, WifiNetwork right)
        {
            int rank = Rank(left.Ssid).CompareTo(Rank(right.Ssid));
            if (rank != 0)
            {
                return rank;
            }

            int connected = left.IsConnected.CompareTo(right.IsConnected);
            if (connected != 0)
            {
                return connected;
            }

            return left.SignalQuality.CompareTo(right.SignalQuality);
        }
    }

    /// <summary>
    /// Builds the WLAN profile XML handed to WlanConnect in temporary-profile mode.
    /// No profile is written to disk, so an unprivileged app can join the controller's
    /// access point without an elevated helper.
    /// </summary>
    public static class WifiProfileBuilder
    {
        private static readonly XNamespace ProfileNamespace =
            "http://www.microsoft.com/networking/WLAN/profile/v1";

        public static bool IsValidPassphrase(string? password) =>
            password is { Length: >= 8 and <= 63 };

        public static string? Build(WifiNetwork network, string? password, out string? error)
        {
            if (network is null)
            {
                error = "No Wi-Fi network was selected.";
                return null;
            }

            return Build(network.Ssid, network.IsSecure, network.SecurityKind, password, out error);
        }

        public static string? Build(
            string ssid,
            bool isSecure,
            WifiSecurityKind securityKind,
            string? password,
            out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ssid))
            {
                error = "The controller's Wi-Fi network has an empty name.";
                return null;
            }

            string? authentication = null;
            string encryption = "none";

            if (!isSecure)
            {
                authentication = "open";
            }
            else
            {
                switch (securityKind)
                {
                    case WifiSecurityKind.WpaPsk:
                        authentication = "WPAPSK";
                        encryption = "AES";
                        break;
                    case WifiSecurityKind.Wpa2Psk:
                        authentication = "WPA2PSK";
                        encryption = "AES";
                        break;
                    case WifiSecurityKind.Wpa3Sae:
                        authentication = "WPA3SAE";
                        encryption = "AES";
                        break;
                }
            }

            if (authentication is null)
            {
                error = $"'{ssid}' uses a security type SyncBox cannot join automatically. Join it once from the Windows Wi-Fi menu.";
                return null;
            }

            if (isSecure)
            {
                if (string.IsNullOrEmpty(password))
                {
                    error = $"'{ssid}' is a secured network. Enter its Wi-Fi password in the interface card, or join it once from the Windows Wi-Fi menu.";
                    return null;
                }

                if (!IsValidPassphrase(password))
                {
                    error = "A Wi-Fi password must be 8 to 63 characters long.";
                    return null;
                }
            }

            var security = new XElement(
                ProfileNamespace + "security",
                new XElement(
                    ProfileNamespace + "authEncryption",
                    new XElement(ProfileNamespace + "authentication", authentication),
                    new XElement(ProfileNamespace + "encryption", encryption),
                    new XElement(ProfileNamespace + "useOneX", "false")));

            if (isSecure)
            {
                security.Add(
                    new XElement(
                        ProfileNamespace + "sharedKey",
                        new XElement(ProfileNamespace + "keyType", "passPhrase"),
                        new XElement(ProfileNamespace + "protected", "false"),
                        new XElement(ProfileNamespace + "keyMaterial", password!)));
            }

            var document = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(
                    ProfileNamespace + "WLANProfile",
                    new XElement(ProfileNamespace + "name", ssid),
                    new XElement(
                        ProfileNamespace + "SSIDConfig",
                        new XElement(
                            ProfileNamespace + "SSID",
                            new XElement(ProfileNamespace + "name", ssid))),
                    new XElement(ProfileNamespace + "connectionType", "ESS"),
                    new XElement(ProfileNamespace + "connectionMode", "manual"),
                    new XElement(ProfileNamespace + "MSM", security)));

            return document.ToString(SaveOptions.DisableFormatting);
        }
    }
}
