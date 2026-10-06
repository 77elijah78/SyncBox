using System;
using System.Collections.Generic;
using SyncBox.Services;
using Xunit;

namespace SyncBox.Tests
{
    public class LedProtocolTests
    {
        [Fact]
        public void FrameSizeMatchesHeaderPlusPixelsPlusCrc()
        {
            Assert.Equal(5 + (58 * 3) + 1, LedProtocol.FrameSize(58));
            Assert.Equal(9, LedProtocol.FrameSize(1));
        }

        [Fact]
        public void Crc8MatchesSmbusCheckValue()
        {
            ReadOnlySpan<byte> data = "123456789"u8;
            Assert.Equal(0xF4, LedProtocol.Crc8(data));
        }

        [Fact]
        public void WriteFrameProducesHeaderLengthPayloadAndCrc()
        {
            var pixels = new byte[6];
            pixels[0] = 10;
            pixels[1] = 20;
            pixels[2] = 30;
            pixels[3] = 40;
            pixels[4] = 50;
            pixels[5] = 60;

            var frame = new byte[LedProtocol.FrameSize(2)];
            LedProtocol.WriteFrame(frame, LedFrameKind.Pixels, 2, pixels);

            Assert.Equal(0xAA, frame[0]);
            Assert.Equal(0x55, frame[1]);
            Assert.Equal((byte)LedFrameKind.Pixels, frame[2]);
            Assert.Equal(2, frame[3]);
            Assert.Equal(0, frame[4]);
            Assert.Equal(10, frame[5]);
            Assert.Equal(60, frame[10]);
            Assert.True(LedProtocol.Verify(frame));
        }

        [Fact]
        public void WriteFrameRejectsMismatchedPayload()
        {
            var frame = new byte[LedProtocol.FrameSize(2)];
            Assert.Throws<ArgumentException>(() => LedProtocol.WriteFrame(frame, LedFrameKind.Pixels, 2, new byte[3]));
        }

        [Fact]
        public void VerifyRejectsCorruptedFrame()
        {
            var frame = new byte[LedProtocol.FrameSize(1)];
            LedProtocol.WriteFrame(frame, LedFrameKind.Pixels, 1, new byte[] { 1, 2, 3 });
            frame[6] ^= 0xFF;
            Assert.False(LedProtocol.Verify(frame));
        }

        [Fact]
        public void ReaderReassemblesFragmentedFrame()
        {
            var reader = new LedFrameReader();
            var received = new List<LedFrame>();
            reader.FrameReceived += received.Add;

            var frame = new byte[LedProtocol.FrameSize(1)];
            LedProtocol.WriteFrame(frame, LedFrameKind.Ack, 1, new byte[] { 9, 9, 9 });

            reader.Append(frame.AsSpan(0, 3));
            Assert.Empty(received);
            reader.Append(frame.AsSpan(3, frame.Length - 3));

            LedFrame only = Assert.Single(received);
            Assert.Equal(LedFrameKind.Ack, only.Kind);
            Assert.Equal(1, only.LedCount);
        }

        [Fact]
        public void ReaderSkipsLeadingGarbage()
        {
            var reader = new LedFrameReader();
            var received = new List<LedFrame>();
            reader.FrameReceived += received.Add;

            var frame = new byte[LedProtocol.FrameSize(1)];
            LedProtocol.WriteFrame(frame, LedFrameKind.Ack, 1, new byte[] { 0, 0, 0 });

            var stream = new byte[frame.Length + 3];
            stream[0] = 0x00;
            stream[1] = 0xAA;
            stream[2] = 0x13;
            frame.CopyTo(stream, 3);

            reader.Append(stream);
            Assert.Single(received);
        }

        [Fact]
        public void ReaderDropsFramesWithBadCrc()
        {
            var reader = new LedFrameReader();
            int count = 0;
            reader.FrameReceived += _ => count++;

            var frame = new byte[LedProtocol.FrameSize(1)];
            LedProtocol.WriteFrame(frame, LedFrameKind.Pixels, 1, new byte[] { 1, 2, 3 });
            frame[^1] ^= 0x5A;

            reader.Append(frame);
            Assert.Equal(0, count);
        }
    }

    public class LedLimitsTests
    {
        [Theory]
        [InlineData(0, 1)]
        [InlineData(-40, 1)]
        [InlineData(19, 19)]
        [InlineData(99999, LedLimits.MaxLedCount)]
        public void ClampKeepsLedCountsInRange(int input, int expected)
        {
            Assert.Equal(expected, LedLimits.Clamp(input));
        }

        [Fact]
        public void ClampBaudFallsBackForUnknownRates()
        {
            Assert.Equal(1000000, LedLimits.ClampBaud(1000000));
            Assert.Equal(115200, LedLimits.ClampBaud(12345));
        }

        [Theory]
        [InlineData("192.168.4.1", true)]
        [InlineData("abc", false)]
        [InlineData("", false)]
        [InlineData("127.0.0.1", false)]
        public void IpValidationRejectsUnusableAddresses(string value, bool expected)
        {
            Assert.Equal(expected, LedLimits.IsValidIp(value));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(70000, false)]
        [InlineData(1234, true)]
        public void PortValidationRejectsOutOfRangePorts(int value, bool expected)
        {
            Assert.Equal(expected, LedLimits.IsValidPort(value));
        }
    }

    public class CaptureRegionTests
    {
        [Fact]
        public void IntersectClampsToBounds()
        {
            var region = new CaptureRegion(0, 0, 100, 100);
            CaptureRegion clipped = region.Intersect(50, 50, 500, 500);

            Assert.Equal(50, clipped.X);
            Assert.Equal(50, clipped.Y);
            Assert.Equal(50, clipped.Width);
            Assert.Equal(50, clipped.Height);
        }

        [Fact]
        public void IntersectWithFullyOffScreenAreaIsEmpty()
        {
            var region = new CaptureRegion(0, 0, 100, 100);
            Assert.True(region.Intersect(5000, 5000, 200, 200).IsEmpty);
        }

        [Fact]
        public void InsetNeverInvertsBounds()
        {
            var region = new CaptureRegion(0, 0, 10, 10);
            CaptureRegion inset = region.Inset(20, 20, 20, 20);

            Assert.True(inset.Width >= 0);
            Assert.True(inset.Height >= 0);
            Assert.True(inset.Width <= region.Width);
            Assert.True(inset.Height <= region.Height);
        }

        [Fact]
        public void InsetAppliesEachEdgeIndependently()
        {
            var region = new CaptureRegion(0, 0, 100, 100);
            CaptureRegion inset = region.Inset(10, 20, 30, 40);

            Assert.Equal(10, inset.X);
            Assert.Equal(20, inset.Y);
            Assert.Equal(60, inset.Width);
            Assert.Equal(40, inset.Height);
        }
    }
}