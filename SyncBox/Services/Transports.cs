using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace SyncBox.Services
{
    public interface ISyncTransport : IDisposable
    {
        string Name { get; }

        bool IsOpen { get; }

        string? LastError { get; }

        void Open();

        void Send(ReadOnlySpan<byte> data);

        void Close();
    }

    public sealed class SerialTransport : ISyncTransport
    {
        private readonly string _portName;
        private readonly int _baudRate;
        private readonly LedFrameReader _reader = new();
        private SerialPort? _port;
        private byte[]? _writeBuffer;
        private bool _disposed;

        public SerialTransport(string portName, int baudRate)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                throw new ArgumentException("A serial port name is required.", nameof(portName));
            }

            _portName = portName.Trim();
            _baudRate = LedLimits.ClampBaud(baudRate);
            _reader.FrameReceived += frame => FrameReceived?.Invoke(frame);
        }

        public event Action<LedFrame>? FrameReceived;

        /// <summary>Raised when the device answers with the plain letter O instead of a frame.</summary>
        public event Action? TextAckReceived;

        public string Name => $"Serial {_portName} @ {_baudRate}";

        public bool IsOpen => _port is { IsOpen: true };

        public string? LastError { get; private set; }

        public static IReadOnlyList<string> AvailablePorts()
        {
            try
            {
                return SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }

            Close();
            _reader.Reset();

            var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 200,
                WriteTimeout = 200,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadBufferSize = 8192,
                WriteBufferSize = 8192
            };

            port.DataReceived += OnDataReceived;

            try
            {
                port.Open();
                _port = port;
                LastError = null;
            }
            catch (Exception ex)
            {
                port.DataReceived -= OnDataReceived;
                port.Dispose();
                LastError = $"Could not open {_portName}: {ex.Message}";
                throw new InvalidOperationException(LastError, ex);
            }
        }

        public void Send(ReadOnlySpan<byte> data)
        {
            if (!IsOpen || _port is null)
            {
                return;
            }

            try
            {
                if (_writeBuffer is null || _writeBuffer.Length != data.Length)
                {
                    _writeBuffer = new byte[data.Length];
                }

                data.CopyTo(_writeBuffer);
                _port.Write(_writeBuffer, 0, data.Length);
            }
            catch (TimeoutException)
            {
                LastError = $"Write to {_portName} timed out.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                LastError = $"Write to {_portName} failed: {ex.Message}";
            }
        }

        public void Close()
        {
            SerialPort? port = _port;
            _port = null;
            if (port is null)
            {
                return;
            }

            try
            {
                port.DataReceived -= OnDataReceived;
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch
            {
            }
            finally
            {
                port.Dispose();
            }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort? port = sender as SerialPort;
            if (port is null || !port.IsOpen)
            {
                return;
            }

            try
            {
                int available = port.BytesToRead;
                if (available <= 0)
                {
                    return;
                }

                var buffer = new byte[Math.Min(available, 1024)];
                int read = port.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
                    int textAcks = LedProtocol.CountTextAcks(chunk);
                    _reader.Append(chunk);

                    // A single letter is enough for the waiting frame loop, the flag is
                    // reused by every acknowledgement in the chunk.
                    for (int index = 0; index < textAcks; index++)
                    {
                        TextAckReceived?.Invoke();
                    }
                }
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
            {
                LastError = $"Read from {_portName} failed: {ex.Message}";
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
        }
    }

    public sealed class UdpTransport : ISyncTransport
    {
        private const int MaxReportedFailures = 5;

        private readonly string _address;
        private readonly int _port;
        private UdpClient? _client;
        private IPEndPoint? _endpoint;
        private int _failureCount;
        private bool _disposed;

        public UdpTransport(string address, int port)
        {
            _address = address?.Trim() ?? string.Empty;
            _port = port;
        }

        public string Name => $"UDP {_address}:{_port}";

        public bool IsOpen => _client is not null && _endpoint is not null;

        public string? LastError { get; private set; }

        public long SentFrames { get; private set; }

        public long FailedFrames { get; private set; }

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }

            Close();

            if (!IPAddress.TryParse(_address, out IPAddress? address))
            {
                LastError = $"'{_address}' is not a valid IP address.";
                throw new ArgumentException(LastError);
            }

            if (!LedLimits.IsValidPort(_port))
            {
                LastError = $"{_port} is not a valid UDP port.";
                throw new ArgumentOutOfRangeException(nameof(_port));
            }

            try
            {
                _client = new UdpClient();
                _client.Client.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.SendTimeout, 200);
                _endpoint = new IPEndPoint(address, _port);
                LastError = null;
            }
            catch (Exception ex)
            {
                Close();
                LastError = "Could not open the UDP socket: " + ex.Message;
                throw new InvalidOperationException(LastError, ex);
            }
        }

        public void Send(ReadOnlySpan<byte> data)
        {
            if (!IsOpen || _client is null || _endpoint is null)
            {
                return;
            }

            try
            {
                _client.Send(data.ToArray(), data.Length, _endpoint);
                SentFrames++;
                LastError = null;
            }
            catch (Exception ex)
            {
                FailedFrames++;
                if (_failureCount++ < MaxReportedFailures)
                {
                    LastError = $"UDP send failed: {ex.Message}";
                }
            }
        }

        public void Close()
        {
            UdpClient? client = _client;
            _client = null;
            _endpoint = null;
            client?.Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
        }
    }
}