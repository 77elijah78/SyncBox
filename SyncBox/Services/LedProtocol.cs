using System;

namespace SyncBox.Services
{
    public static class LedLimits
    {
        public const int MinLedCount = 1;
        public const int MaxLedCount = 1024;

        public static int Clamp(int value) => Math.Clamp(value, MinLedCount, MaxLedCount);

        public static int ClampBaud(int value) => value is 9600 or 19200 or 38400 or 57600 or 115200 or 230400 or 460800 or 500000 or 921600 or 1000000 or 1500000 or 2000000
            ? value
            : 115200;

        public static bool IsValidPort(int value) => value is > 0 and <= 65535;

        public static bool IsValidIp(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && System.Net.IPAddress.TryParse(value.Trim(), out _)
                && !System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(value.Trim()));
        }
    }

    public enum LedFrameKind : byte
    {
        Pixels = 0x01,
        Ack = 0x02
    }

    public static class LedProtocol
    {
        public const byte Header0 = 0xAA;
        public const byte Header1 = 0x55;
        public const int HeaderSize = 5;
        public const int CrcSize = 1;
        public const int Overhead = HeaderSize + CrcSize;
        public const int MaxFrameBytes = 4096;

        public static int FrameSize(int ledCount) => HeaderSize + (ledCount * 3) + CrcSize;

        public static byte[] CreateFrame(LedFrameKind kind, int ledCount, ReadOnlySpan<byte> pixels)
        {
            var frame = new byte[FrameSize(ledCount)];
            WriteFrame(frame, kind, ledCount, pixels);
            return frame;
        }

        public static void WriteFrame(Span<byte> frame, LedFrameKind kind, int ledCount, ReadOnlySpan<byte> pixels)
        {
            int expected = ledCount * 3;
            if (pixels.Length != expected)
            {
                throw new ArgumentException($"Expected {expected} pixel bytes, got {pixels.Length}.", nameof(pixels));
            }

            int required = FrameSize(ledCount);
            if (frame.Length < required)
            {
                throw new ArgumentException($"Frame buffer holds {frame.Length} bytes, {required} required.", nameof(frame));
            }

            frame[0] = Header0;
            frame[1] = Header1;
            frame[2] = (byte)kind;
            frame[3] = (byte)(ledCount & 0xFF);
            frame[4] = (byte)((ledCount >> 8) & 0xFF);
            pixels.CopyTo(frame.Slice(HeaderSize, expected));
            frame[required - 1] = Crc8(frame.Slice(0, required - 1));
        }

        public static byte Crc8(ReadOnlySpan<byte> data)
        {
            byte crc = 0x00;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
                }
            }

            return crc;
        }

        public static bool Verify(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < Overhead)
            {
                return false;
            }

            if (frame[0] != Header0 || frame[1] != Header1)
            {
                return false;
            }

            return frame[^1] == Crc8(frame[..^1]);
        }
    }

    public readonly struct LedFrame
    {
        public LedFrame(LedFrameKind kind, int ledCount, int startIndex, int length)
        {
            Kind = kind;
            LedCount = ledCount;
            StartIndex = startIndex;
            Length = length;
        }

        public LedFrameKind Kind { get; }

        public int LedCount { get; }

        public int StartIndex { get; }

        public int Length { get; }
    }

    public sealed class LedFrameReader
    {
        private const int MaxBuffer = 8192;
        private readonly byte[] _buffer = new byte[MaxBuffer];
        private int _length;

        public event Action<LedFrame>? FrameReceived;

        public void Append(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return;
            }

            if (data.Length >= MaxBuffer)
            {
                _length = 0;
            }

            int room = MaxBuffer - _length;
            if (data.Length > room)
            {
                _length = 0;
                room = MaxBuffer;
            }

            data[..Math.Min(data.Length, room)].CopyTo(_buffer.AsSpan(_length));
            _length += Math.Min(data.Length, room);
            Parse();
        }

        public void Reset() => _length = 0;

        private void Parse()
        {
            int start = 0;
            while (_length - start >= LedProtocol.HeaderSize)
            {
                int i = start;
                while (i < _length && (_buffer[i] != LedProtocol.Header0 || _buffer[i + 1] != LedProtocol.Header1))
                {
                    i++;
                }

                if (i + 1 >= _length)
                {
                    start = i;
                    break;
                }

                int offset = i;
                if (i + LedProtocol.HeaderSize > _length)
                {
                    start = offset;
                    break;
                }

                var kind = (LedFrameKind)_buffer[offset + 2];
                int ledCount = _buffer[offset + 3] | (_buffer[offset + 4] << 8);
                int total = LedProtocol.HeaderSize + (ledCount * 3) + LedProtocol.CrcSize;

                if (ledCount > LedLimits.MaxLedCount || total > LedProtocol.MaxFrameBytes)
                {
                    start = offset + 1;
                    continue;
                }

                if (offset + total > _length)
                {
                    start = offset;
                    break;
                }

                if (LedProtocol.Verify(_buffer.AsSpan(offset, total)))
                {
                    FrameReceived?.Invoke(new LedFrame(kind, ledCount, 0, ledCount));
                    start = offset + total;
                }
                else
                {
                    start = offset + 1;
                }
            }

            if (start > 0)
            {
                Buffer.BlockCopy(_buffer, start, _buffer, 0, _length - start);
                _length -= start;
            }
        }
    }
}