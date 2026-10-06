using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace SyncBox.Services
{
    public sealed class SyncBoxDevice
    {
        public SyncBoxDevice(string portName, string firmwareVersion, int baudRate, string rawResponse)
        {
            PortName = portName;
            FirmwareVersion = firmwareVersion;
            BaudRate = baudRate;
            RawResponse = rawResponse;
        }

        public string PortName { get; }

        public string FirmwareVersion { get; }

        public int BaudRate { get; }

        public string RawResponse { get; }

        public override string ToString() => $"{PortName} - {FirmwareVersion} @ {BaudRate}";
    }

    public delegate string? PortProbe(string portName, int baudRate, TimeSpan timeout, CancellationToken token);

    public static class DeviceDiscovery
    {
        public const string FeedbackPrefix = "Sync Box";

        private static readonly byte[] PreambleBytes = { 0xAF, 0x05, 0xD1 };

        public static ReadOnlyMemory<byte> Preamble => PreambleBytes;

        public static IReadOnlyList<int> DefaultBaudRates { get; } = new[] { 1000000, 115200, 500000 };

        public static string? ExtractFirmwareVersion(string? response)
        {
            if (string.IsNullOrEmpty(response))
            {
                return null;
            }

            int start = response.IndexOf(FeedbackPrefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            int end = start;
            while (end < response.Length && !char.IsControl(response[end]))
            {
                end++;
            }

            string version = response[start..end].Trim();
            return version.Length == 0 ? null : version;
        }

        public static IReadOnlyList<SyncBoxDevice> Scan(
            IReadOnlyList<string> candidatePorts,
            IReadOnlyList<int>? baudRates = null,
            string? preferredPort = null,
            TimeSpan? timeout = null,
            CancellationToken token = default,
            PortProbe? prober = null)
        {
            if (candidatePorts.Count == 0)
            {
                return Array.Empty<SyncBoxDevice>();
            }

            IReadOnlyList<int> bauds = NormalizeBauds(baudRates);
            TimeSpan wait = timeout ?? TimeSpan.FromMilliseconds(600);
            PortProbe probe = prober ?? ProbePort;
            var found = new List<SyncBoxDevice>();

            for (int pass = 0; pass < bauds.Count && found.Count == 0; pass++)
            {
                int baud = bauds[pass];

                foreach (string port in OrderPorts(candidatePorts, preferredPort))
                {
                    if (token.IsCancellationRequested)
                    {
                        return found;
                    }

                    string? response;
                    try
                    {
                        response = probe(port, baud, wait, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;
                    }

                    string? version = ExtractFirmwareVersion(response);
                    if (version is not null)
                    {
                        found.Add(new SyncBoxDevice(port, version, baud, response!));
                    }
                }
            }

            return found;
        }

        public static string? ProbePort(string portName, int baudRate, TimeSpan timeout, CancellationToken token)
        {
            SerialPort? port = null;
            try
            {
                port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 60,
                    WriteTimeout = 250,
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadBufferSize = 2048,
                    WriteBufferSize = 2048
                };

                port.Open();
                port.Write(PreambleBytes, 0, PreambleBytes.Length);
                port.BaseStream.Flush();

                var response = new StringBuilder();
                var chunk = new byte[256];
                DateTime deadline = DateTime.UtcNow + timeout;

                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                {
                    try
                    {
                        int read = port.Read(chunk, 0, chunk.Length);
                        if (read > 0)
                        {
                            response.Append(Encoding.ASCII.GetString(chunk, 0, read));
                            string? version = ExtractFirmwareVersion(response.ToString());
                            if (version is not null)
                            {
                                return version;
                            }
                        }
                    }
                    catch (TimeoutException)
                    {
                    }
                }

                return ExtractFirmwareVersion(response.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
            finally
            {
                if (port is not null)
                {
                    try
                    {
                        if (port.IsOpen)
                        {
                            port.Close();
                        }
                    }
                    catch
                    {
                    }

                    port.Dispose();
                }
            }
        }

        private static IReadOnlyList<int> NormalizeBauds(IReadOnlyList<int>? baudRates)
        {
            if (baudRates is null || baudRates.Count == 0)
            {
                return DefaultBaudRates;
            }

            var unique = new List<int>();
            foreach (int baud in baudRates)
            {
                if (LedLimits.ClampBaud(baud) == baud && !unique.Contains(baud))
                {
                    unique.Add(baud);
                }
            }

            return unique.Count == 0 ? DefaultBaudRates : unique;
        }

        private static IEnumerable<string> OrderPorts(IReadOnlyList<string> ports, string? preferredPort)
        {
            if (!string.IsNullOrWhiteSpace(preferredPort))
            {
                foreach (string port in ports)
                {
                    if (string.Equals(port, preferredPort, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return port;
                        break;
                    }
                }
            }

            foreach (string port in ports)
            {
                if (!string.Equals(port, preferredPort, StringComparison.OrdinalIgnoreCase))
                {
                    yield return port;
                }
            }
        }
    }
}