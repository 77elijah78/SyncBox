using System;
using System.IO;
using System.Runtime.InteropServices;
using SyncBox.Services;
using Xunit;

namespace SyncBox.Tests
{
    public unsafe class LedSamplerTests
    {
        [Fact]
        public void ConfigureClampsCountsAndReportsRingSize()
        {
            var sampler = new LedSampler();
            sampler.Configure(0, -5);

            Assert.Equal(1, sampler.Horizontal);
            Assert.Equal(1, sampler.Vertical);
            Assert.Equal(4, sampler.LedCount);
        }

        [Fact]
        public void UniformSurfaceProducesUniformOutput()
        {
            var sampler = new LedSampler();
            sampler.Configure(19, 10);
            var settings = FlatSettings();

            var buffer = new byte[64 * 64 * 4];
            Fill(buffer, 255, 255, 255);

            var destination = new byte[sampler.LedCount * 3];
            bool ok = Sample(sampler, buffer, 64, 64, default, settings, destination);

            Assert.True(ok);
            Assert.All(destination, value => Assert.Equal(255, value));
        }

        [Fact]
        public void BlackSurfaceProducesZeroOutput()
        {
            var sampler = new LedSampler();
            sampler.Configure(19, 10);
            var settings = FlatSettings();

            var buffer = new byte[64 * 64 * 4];
            var destination = new byte[sampler.LedCount * 3];

            Assert.True(Sample(sampler, buffer, 64, 64, default, settings, destination));
            Assert.All(destination, value => Assert.Equal(0, value));
        }

        [Fact]
        public void CinematicModeSkipsLetterboxBars()
        {
            var sampler = new LedSampler();
            sampler.Configure(19, 10);

            var settings = FlatSettings();
            settings.Cinematic = true;

            var buffer = new byte[64 * 64 * 4];
            FillArea(buffer, 64, 16, 16, 32, 32, 255, 255, 255);

            var destination = new byte[sampler.LedCount * 3];
            Assert.True(Sample(sampler, buffer, 64, 64, default, settings, destination));
            Assert.All(destination, value => Assert.Equal(255, value));
        }

        [Fact]
        public void WithoutCinematicModeBarsAreSampled()
        {
            var sampler = new LedSampler();
            sampler.Configure(19, 10);

            var settings = FlatSettings();
            settings.Cinematic = false;

            var buffer = new byte[64 * 64 * 4];
            FillArea(buffer, 64, 16, 16, 32, 32, 255, 255, 255);

            var destination = new byte[sampler.LedCount * 3];
            Assert.True(Sample(sampler, buffer, 64, 64, default, settings, destination));
            Assert.All(destination, value => Assert.Equal(0, value));
        }

        [Theory]
        [InlineData(5000, 5000)]
        [InlineData(-9000, -9000)]
        [InlineData(1920, 0)]
        public void RegionOutsideTheSurfaceFallsBackInsteadOfReadingOutOfBounds(int x, int y)
        {
            var sampler = new LedSampler();
            sampler.Configure(19, 10);
            var settings = FlatSettings();

            var buffer = new byte[64 * 64 * 4];
            Fill(buffer, 255, 255, 255);

            var destination = new byte[sampler.LedCount * 3];
            bool ok = Sample(sampler, buffer, 64, 64, new CaptureRegion(x, y, 800, 600), settings, destination);

            Assert.True(ok);
            Assert.All(destination, value => Assert.Equal(255, value));
        }

        [Fact]
        public void SurfaceWithNegativeVirtualOriginIsSampledCorrectly()
        {
            var sampler = new LedSampler();
            sampler.Configure(4, 4);
            var settings = FlatSettings();

            var buffer = new byte[32 * 32 * 4];
            Fill(buffer, 0, 0, 255);

            var destination = new byte[sampler.LedCount * 3];
            bool ok = Sample(sampler, buffer, 32, 32, new CaptureRegion(-1920, 0, 32, 32), settings, destination, -1920, 0);

            Assert.True(ok);
            Assert.Equal(255, destination[2]);
            Assert.Equal(0, destination[0]);
        }

        [Fact]
        public void SmoothingDampsAbruptChanges()
        {
            var sampler = new LedSampler();
            sampler.Configure(1, 1);

            var settings = FlatSettings();
            settings.Smoothing = 0.9;

            var buffer = new byte[16 * 16 * 4];
            var destination = new byte[sampler.LedCount * 3];

            Fill(buffer, 255, 255, 255);
            Sample(sampler, buffer, 16, 16, default, settings, destination);
            byte bright = destination[0];

            Fill(buffer, 0, 0, 0);
            Sample(sampler, buffer, 16, 16, default, settings, destination);

            Assert.Equal(255, bright);
            Assert.True(destination[0] < bright, "Smoothing should lag behind an abrupt change to black.");
        }

        [Fact]
        public void BrightnessAndGammaShapeTheCurve()
        {
            var sampler = new LedSampler();
            sampler.Configure(1, 1);

            var settings = FlatSettings();
            settings.Brightness = 0.5;

            var buffer = new byte[16 * 16 * 4];
            Fill(buffer, 255, 255, 255);

            var destination = new byte[sampler.LedCount * 3];
            Sample(sampler, buffer, 16, 16, default, settings, destination);

            Assert.InRange(destination[0], 180, 200);
        }

        [Fact]
        public void OversizedRegionCannotOverflowTheRingBuffer()
        {
            var sampler = new LedSampler();
            sampler.Configure(64, 64);
            var settings = FlatSettings();

            var buffer = new byte[8 * 8 * 4];
            var destination = new byte[sampler.LedCount * 3];

            Assert.True(Sample(sampler, buffer, 8, 8, default, settings, destination));
            Assert.Equal(256 * 3, destination.Length);
        }

        private static LedRenderSettings FlatSettings() => new()
        {
            Brightness = 1.0,
            Gamma = 1.0,
            Saturation = 1.0,
            Smoothing = 0.0,
            Cinematic = false,
            BarThreshold = 0.06,
            AverageSize = 1
        };

        private static bool Sample(LedSampler sampler, byte[] buffer, int width, int height,
            CaptureRegion requested, LedRenderSettings settings, byte[] destination, int originX = 0, int originY = 0)
        {
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var surface = new CapturedSurface((byte*)handle.AddrOfPinnedObject(), width * 4, originX, originY, width, height);
                return sampler.Sample(new[] { surface }, requested, settings, destination);
            }
            finally
            {
                handle.Free();
            }
        }

        private static void Fill(byte[] buffer, byte red, byte green, byte blue)
        {
            for (int i = 0; i < buffer.Length; i += 4)
            {
                buffer[i] = blue;
                buffer[i + 1] = green;
                buffer[i + 2] = red;
                buffer[i + 3] = 255;
            }
        }

        private static void FillArea(byte[] buffer, int width, int x, int y, int areaWidth, int areaHeight,
            byte red, byte green, byte blue)
        {
            for (int row = y; row < y + areaHeight; row++)
            {
                for (int column = x; column < x + areaWidth; column++)
                {
                    int index = ((row * width) + column) * 4;
                    buffer[index] = blue;
                    buffer[index + 1] = green;
                    buffer[index + 2] = red;
                    buffer[index + 3] = 255;
                }
            }
        }
    }

    public class SettingsServiceTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "syncbox-tests", Guid.NewGuid().ToString("N"), "settings.json");

        [Fact]
        public void MissingFileYieldsDefaults()
        {
            var service = new SettingsService(_path);
            AppSettings settings = service.Load();

            Assert.Equal(19, settings.HorizontalLedCount);
            Assert.Equal(10, settings.VerticalLedCount);
            Assert.True(settings.UseGpu);
        }

        [Fact]
        public void RoundTripPreservesValues()
        {
            var service = new SettingsService(_path);
            var written = new AppSettings
            {
                HorizontalLedCount = 30,
                VerticalLedCount = 12,
                ComPort = "COM7",
                BaudRate = 115200,
                UseWifi = true,
                IpAddress = "10.0.0.5",
                Port = 5000,
                Brightness = 0.42,
                Gamma = 1.8
            };

            Assert.True(service.TrySave(written));
            AppSettings loaded = service.Load();

            Assert.Equal(30, loaded.HorizontalLedCount);
            Assert.Equal(12, loaded.VerticalLedCount);
            Assert.Equal("COM7", loaded.ComPort);
            Assert.Equal(115200, loaded.BaudRate);
            Assert.True(loaded.UseWifi);
            Assert.Equal("10.0.0.5", loaded.IpAddress);
            Assert.Equal(5000, loaded.Port);
            Assert.Equal(0.42, loaded.Brightness, 3);
            Assert.Equal(1.8, loaded.Gamma, 3);
        }

        [Fact]
        public void CorruptFileFallsBackToDefaults()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{ this is not json");

            AppSettings loaded = new SettingsService(_path).Load();

            Assert.Equal(19, loaded.HorizontalLedCount);
        }

        [Fact]
        public void SanitizedClampsHostileValues()
        {
            var settings = new AppSettings
            {
                HorizontalLedCount = -10,
                VerticalLedCount = 999999,
                BaudRate = 7,
                Port = 99999,
                Brightness = 12.0,
                Gamma = -4,
                Saturation = 99,
                Smoothing = 5,
                AverageSize = 40,
                TargetFps = -10
            }.Sanitized();

            Assert.Equal(1, settings.HorizontalLedCount);
            Assert.Equal(LedLimits.MaxLedCount, settings.VerticalLedCount);
            Assert.Equal(115200, settings.BaudRate);
            Assert.Equal(1234, settings.Port);
            Assert.Equal(1.0, settings.Brightness);
            Assert.Equal(0.2, settings.Gamma);
            Assert.Equal(3.0, settings.Saturation);
            Assert.Equal(0.98, settings.Smoothing);
            Assert.Equal(5, settings.AverageSize);
            Assert.Equal(0, settings.TargetFps);
        }

        [Fact]
        public void FrameRateLimitIsClampedToASaneRange()
        {
            Assert.Equal(240, new AppSettings { TargetFps = 5000 }.Sanitized().TargetFps);
            Assert.Equal(0, new AppSettings { TargetFps = -10 }.Sanitized().TargetFps);
            Assert.Equal(60, new AppSettings().Sanitized().TargetFps);
        }

        public void Dispose()
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public class ConverterTests
    {
        [Fact]
        public void InverseConverterTreatsNullAsUnchecked()
        {
            var converter = new InverseBoolConverter();
            Assert.Equal(false, converter.Convert(null, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(true, converter.Convert(false, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(false, converter.Convert(true, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(true, converter.ConvertBack(false, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void InverseConverterIgnoresNonBooleanUpdates()
        {
            var converter = new InverseBoolConverter();
            Assert.Equal(System.Windows.Data.Binding.DoNothing,
                converter.ConvertBack("nonsense", typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void VisibilityConverterHonoursInverseParameter()
        {
            var converter = new BooleanToVisibilityConverter();
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;

            Assert.Equal(System.Windows.Visibility.Visible, converter.Convert(true, typeof(System.Windows.Visibility), null, culture));
            Assert.Equal(System.Windows.Visibility.Collapsed, converter.Convert(true, typeof(System.Windows.Visibility), "Inverse", culture));
            Assert.Equal(System.Windows.Visibility.Collapsed, converter.Convert(null, typeof(System.Windows.Visibility), null, culture));
        }

        [Fact]
        public void ConnectionConverterReturnsGreenOnlyWhenConnected()
        {
            var converter = new ConnectionColorConverter();
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;

            var online = (System.Windows.Media.SolidColorBrush)converter.Convert(true, typeof(System.Windows.Media.Brush), null, culture);
            var offline = (System.Windows.Media.SolidColorBrush)converter.Convert(false, typeof(System.Windows.Media.Brush), null, culture);

            Assert.Equal(System.Windows.Media.Colors.SpringGreen, online.Color);
            Assert.Equal(System.Windows.Media.Colors.Crimson, offline.Color);
            Assert.True(online.IsFrozen);
            Assert.Same(online, converter.Convert(true, typeof(System.Windows.Media.Brush), null, culture));
        }
    }
}