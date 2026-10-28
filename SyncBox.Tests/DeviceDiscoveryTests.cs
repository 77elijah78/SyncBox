using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SyncBox.Services;
using Xunit;

namespace SyncBox.Tests
{
    public class DeviceDiscoveryTests
    {
        [Fact]
        public void PreambleMatchesTheControllerHandshake()
        {
            Assert.Equal(new byte[] { 0xAF, 0x05, 0xD1 }, DeviceDiscovery.Preamble.ToArray());
        }

        [Theory]
        [InlineData("Sync Box V1.0", "Sync Box V1.0")]
        [InlineData("Sync Box V1.0\r\n", "Sync Box V1.0")]
        [InlineData("booting...\nSync Box V1.0\n$ ", "Sync Box V1.0")]
        [InlineData("noise", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("esptool.py v4.5", null)]
        public void FirmwareVersionIsExtractedFromTheFeedback(string? response, string? expected)
        {
            Assert.Equal(expected, DeviceDiscovery.ExtractFirmwareVersion(response));
        }

        [Fact]
        public void ScanReturnsNothingWhenThereAreNoPorts()
        {
            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                Array.Empty<string>(),
                new[] { 1000000 },
                null,
                TimeSpan.FromMilliseconds(10));

            Assert.Empty(found);
        }

        [Fact]
        public void ScanProbesThePreferredPortFirst()
        {
            var probed = new List<string>();
            PortProbe prober = (port, baud, timeout, token) =>
            {
                probed.Add(port + "@" + baud);
                return port == "COM7" ? "Sync Box V1.0" : null;
            };

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3", "COM7", "COM11" },
                new[] { 1000000 },
                "COM7",
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            SyncBoxDevice device = Assert.Single(found);
            Assert.Equal("COM7", device.PortName);
            Assert.Equal("Sync Box V1.0", device.FirmwareVersion);
            Assert.Equal(1000000, device.BaudRate);
            Assert.Equal("COM7@1000000", probed[0]);
        }

        [Fact]
        public void ScanFallsBackToOtherPortsWhenThePreferredOneIsSilent()
        {
            var probed = new List<string>();
            PortProbe prober = (port, baud, timeout, token) =>
            {
                probed.Add(port);
                return port == "COM3" ? "Sync Box V1.0" : null;
            };

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3", "COM7" },
                new[] { 1000000 },
                "COM7",
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            Assert.Equal("COM3", Assert.Single(found).PortName);
            Assert.Equal(new[] { "COM7", "COM3" }, probed);
        }

        [Fact]
        public void ScanTriesTheNextBaudRateOnlyAfterTheFirstOneFindsNothing()
        {
            var probed = new List<(string Port, int Baud)>();
            PortProbe prober = (port, baud, timeout, token) =>
            {
                probed.Add((port, baud));
                return port == "COM3" && baud == 115200 ? "Sync Box V1.0" : null;
            };

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3" },
                new[] { 1000000, 115200 },
                null,
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            SyncBoxDevice device = Assert.Single(found);
            Assert.Equal(115200, device.BaudRate);
            Assert.Equal(new[] { ("COM3", 1000000), ("COM3", 115200) }, probed);
        }

        [Fact]
        public void ScanStopsAtTheFirstBaudRateThatAnswers()
        {
            int calls = 0;
            PortProbe prober = (port, baud, timeout, token) =>
            {
                calls++;
                return "Sync Box V1.0";
            };

            DeviceDiscovery.Scan(
                new[] { "COM3" },
                new[] { 1000000, 115200, 500000 },
                null,
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            Assert.Equal(1, calls);
        }

        [Fact]
        public void ScanReportsEveryControllerItFinds()
        {
            PortProbe prober = (port, baud, timeout, token) =>
                port.StartsWith("COM1", StringComparison.Ordinal) ? "Sync Box V1.0" : null;

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3", "COM11", "COM15" },
                new[] { 1000000 },
                null,
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            Assert.Equal(new[] { "COM11", "COM15" }, found.Select(d => d.PortName).ToArray());
        }

        [Fact]
        public void ScanSurvivesAProbeThatThrows()
        {
            PortProbe prober = (port, baud, timeout, token) =>
                port == "COM3" ? throw new InvalidOperationException("busy") : "Sync Box V1.0";

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3", "COM7" },
                new[] { 1000000 },
                null,
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            Assert.Equal("COM7", Assert.Single(found).PortName);
        }

        [Fact]
        public void ScanIgnoresPortsThatAnswerWithSomethingElse()
        {
            PortProbe prober = (port, baud, timeout, token) => port == "COM3" ? "Arduino Leonardo on COM3" : null;

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3" },
                new[] { 1000000 },
                null,
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            Assert.Empty(found);
        }

        [Fact]
        public void CancellationStopsTheScan()
        {
            using var source = new CancellationTokenSource();
            source.Cancel();

            int calls = 0;
            PortProbe prober = (port, baud, timeout, token) =>
            {
                calls++;
                return "Sync Box V1.0";
            };

            IReadOnlyList<SyncBoxDevice> found = DeviceDiscovery.Scan(
                new[] { "COM3", "COM7" },
                new[] { 1000000 },
                null,
                TimeSpan.FromMilliseconds(10),
                source.Token,
                prober);

            Assert.Empty(found);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void ProbingAPortThatCannotBeOpenedReturnsNullInsteadOfThrowing()
        {
            string? result = DeviceDiscovery.ProbePort("COM99", 115200, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.Null(result);
        }

        [Fact]
        public void DuplicatePortsAreProbedOnlyOnce()
        {
            var probed = new List<string>();
            PortProbe prober = (port, baud, timeout, token) =>
            {
                probed.Add(port);
                return null;
            };

            DeviceDiscovery.Scan(
                new[] { "COM7", "COM3", "COM7" },
                new[] { 1000000 },
                "com7",
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                prober);

            Assert.Equal(new[] { "COM7", "COM3" }, probed);
        }
    }

    public class DeviceSettingsTests : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "syncbox-discovery",
            Guid.NewGuid().ToString("N"),
            "settings.json");

        [Fact]
        public void TheBaudRateIsAlwaysStoredAsOneMillion()
        {
            var service = new SettingsService(_path);
            Assert.Equal(1000000, service.Load().BaudRate);

            service.TrySave(new AppSettings { BaudRate = 115200 });
            Assert.Equal(1000000, service.Load().BaudRate);
        }

        public void Dispose()
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (directory is not null && System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}