using System;
using System.Threading;

namespace SyncBox.Services
{
    public sealed class LedRenderSettings
    {
        public double Brightness { get; set; } = 0.85;

        public double Gamma { get; set; } = 1.0;

        public double Saturation { get; set; } = 1.35;

        public double Smoothing { get; set; } = 0.7;

        public bool Cinematic { get; set; } = true;

        public double BarThreshold { get; set; } = 0.06;

        public int AverageSize { get; set; } = 3;

        public LedRenderSettings Clone() => (LedRenderSettings)MemberwiseClone();

        public void CopyFrom(LedRenderSettings other)
        {
            Brightness = other.Brightness;
            Gamma = other.Gamma;
            Saturation = other.Saturation;
            Smoothing = other.Smoothing;
            Cinematic = other.Cinematic;
            BarThreshold = other.BarThreshold;
            AverageSize = other.AverageSize;
        }
    }

    public readonly struct CaptureRegion
    {
        public CaptureRegion(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        public int Right => X + Width;

        public int Bottom => Y + Height;

        public bool IsEmpty => Width <= 0 || Height <= 0;

        public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

        public CaptureRegion Intersect(int x, int y, int width, int height)
        {
            int left = Math.Max(X, x);
            int top = Math.Max(Y, y);
            int right = Math.Min(Right, x + width);
            int bottom = Math.Min(Bottom, y + height);
            return new CaptureRegion(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        public CaptureRegion Inset(int left, int top, int right, int bottom)
        {
            int maxHorizontal = Math.Max(0, Width / 2);
            int maxVertical = Math.Max(0, Height / 2);
            int newLeft = X + Math.Clamp(left, 0, maxHorizontal);
            int newTop = Y + Math.Clamp(top, 0, maxVertical);
            int newRight = Math.Max(newLeft, Right - Math.Clamp(right, 0, maxHorizontal));
            int newBottom = Math.Max(newTop, Bottom - Math.Clamp(bottom, 0, maxVertical));
            return new CaptureRegion(newLeft, newTop, newRight - newLeft, newBottom - newTop);
        }
    }

    public sealed class LedSampler
    {
        private const int BarSamplesPerAxis = 16;
        private const double MaxBarFraction = 0.45;
        private const int MinDisplayExtent = 4;

        private static readonly double[] SrgbToLinearLut = BuildSrgbToLinearLut();

        private double[] _smoothing = Array.Empty<double>();
        private int _horizontal = 19;
        private int _vertical = 10;
        private int _resetRequested;
        private int _primedCount;

        public int Horizontal => _horizontal;

        public int Vertical => _vertical;

        public int LedCount => (_horizontal + _vertical) * 2;

        public void Configure(int horizontal, int vertical)
        {
            _horizontal = LedLimits.Clamp(horizontal);
            _vertical = LedLimits.Clamp(vertical);
            int required = LedCount * 3;
            if (_smoothing.Length != required)
            {
                _smoothing = new double[required];
            }

            _primedCount = 0;
        }

        public void Reset()
        {
            Array.Clear(_smoothing);
            _primedCount = 0;
        }

        public void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);

        public unsafe bool Sample(CapturedSurface[] surfaces, CaptureRegion requested, LedRenderSettings settings, Span<byte> destination)
        {
            if (surfaces.Length == 0)
            {
                return false;
            }

            if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
            {
                Array.Clear(_smoothing);
                _primedCount = 0;
            }

            if (destination.Length < LedCount * 3)
            {
                return false;
            }

            CaptureRegion bounds = Union(surfaces);
            CaptureRegion region = requested.IsEmpty ? bounds : requested.Intersect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (region.IsEmpty)
            {
                region = bounds;
            }

            if (settings.Cinematic)
            {
                region = ApplyCinematicBars(surfaces, region, settings.BarThreshold);
            }

            int count = LedCount;
            double smoothing = Math.Clamp(settings.Smoothing, 0.0, 0.98);
            double saturation = settings.Saturation;
            double brightness = Math.Clamp(settings.Brightness, 0.0, 1.0);
            double gamma = Math.Clamp(settings.Gamma, 0.2, 5.0);
            int average = Math.Clamp(settings.AverageSize, 1, 5);

            int index = 0;
            for (int i = 0; i < _horizontal; i++)
            {
                double t = (_horizontal == 1) ? 0.5 : (i + 0.5) / _horizontal;
                int x = region.X + (int)Math.Round(t * (region.Width - 1));
                Write(surfaces, destination, x, region.Y, average, index, saturation, brightness, gamma, smoothing);
                index += 3;
            }

            for (int i = 0; i < _vertical; i++)
            {
                double t = (_vertical == 1) ? 0.5 : (i + 0.5) / _vertical;
                int y = region.Y + (int)Math.Round(t * (region.Height - 1));
                Write(surfaces, destination, region.Right - 1, y, average, index, saturation, brightness, gamma, smoothing);
                index += 3;
            }

            for (int i = 0; i < _horizontal; i++)
            {
                double t = (_horizontal == 1) ? 0.5 : (i + 0.5) / _horizontal;
                int x = region.Right - 1 - (int)Math.Round(t * (region.Width - 1));
                Write(surfaces, destination, x, region.Bottom - 1, average, index, saturation, brightness, gamma, smoothing);
                index += 3;
            }

            for (int i = 0; i < _vertical; i++)
            {
                double t = (_vertical == 1) ? 0.5 : (i + 0.5) / _vertical;
                int y = region.Bottom - 1 - (int)Math.Round(t * (region.Height - 1));
                Write(surfaces, destination, region.X, y, average, index, saturation, brightness, gamma, smoothing);
                index += 3;
            }

            return index >= count * 3;
        }

        private unsafe void Write(CapturedSurface[] surfaces, Span<byte> destination, int x, int y, int average, int index,
            double saturation, double brightness, double gamma, double smoothing)
        {
            AverageRgb(surfaces, x, y, average, out double r, out double g, out double b);

            r = SrgbToLinearLut[(int)Math.Clamp(r * 255.0 + 0.5, 0.0, 255.0)];
            g = SrgbToLinearLut[(int)Math.Clamp(g * 255.0 + 0.5, 0.0, 255.0)];
            b = SrgbToLinearLut[(int)Math.Clamp(b * 255.0 + 0.5, 0.0, 255.0)];

            if (Math.Abs(saturation - 1.0) > 0.0001)
            {
                double luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                r = luminance + ((r - luminance) * saturation);
                g = luminance + ((g - luminance) * saturation);
                b = luminance + ((b - luminance) * saturation);
            }

            r = Math.Clamp(r, 0.0, 1.0);
            g = Math.Clamp(g, 0.0, 1.0);
            b = Math.Clamp(b, 0.0, 1.0);

            if (_primedCount >= _smoothing.Length)
            {
                r = (_smoothing[index] * smoothing) + (r * (1.0 - smoothing));
                g = (_smoothing[index + 1] * smoothing) + (g * (1.0 - smoothing));
                b = (_smoothing[index + 2] * smoothing) + (b * (1.0 - smoothing));
            }
            else if (index + 3 >= _smoothing.Length)
            {
                _primedCount = _smoothing.Length;
            }

            _smoothing[index] = r;
            _smoothing[index + 1] = g;
            _smoothing[index + 2] = b;

            destination[index] = ToByte(Math.Pow(r * brightness, gamma));
            destination[index + 1] = ToByte(Math.Pow(g * brightness, gamma));
            destination[index + 2] = ToByte(Math.Pow(b * brightness, gamma));
        }

        private static byte ToByte(double linear)
        {
            double encoded = Encode(Math.Clamp(linear, 0.0, 1.0));
            return (byte)Math.Clamp((int)Math.Round(encoded * 255.0), 0, 255);
        }

        private static double Encode(double linear) =>
            linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;

        private static double[] BuildSrgbToLinearLut()
        {
            var lut = new double[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                lut[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return lut;
        }

        private static unsafe void AverageRgb(CapturedSurface[] surfaces, int x, int y, int size,
            out double r, out double g, out double b)
        {
            r = g = b = 0.0;

            for (int s = 0; s < surfaces.Length; s++)
            {
                ref readonly CapturedSurface surface = ref surfaces[s];
                if (!surface.Contains(x, y))
                {
                    continue;
                }

                int half = size / 2;
                int x0 = Math.Clamp(x - half, 0, surface.Width - 1);
                int x1 = Math.Clamp(x + half, 0, surface.Width - 1);
                int y0 = Math.Clamp(y - half, 0, surface.Height - 1);
                int y1 = Math.Clamp(y + half, 0, surface.Height - 1);

                double sumR = 0.0;
                double sumG = 0.0;
                double sumB = 0.0;
                int count = 0;

                for (int py = y0; py <= y1; py++)
                {
                    for (int px = x0; px <= x1; px++)
                    {
                        byte* pixel = surface.Pixel(px, py);
                        sumB += pixel[0];
                        sumG += pixel[1];
                        sumR += pixel[2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                r = sumR / count / 255.0;
                g = sumG / count / 255.0;
                b = sumB / count / 255.0;
                return;
            }
        }

        private static unsafe double RowLuma(CapturedSurface[] surfaces, int y, int left, int right)
        {
            double sum = 0.0;
            int count = 0;
            int span = Math.Max(1, right - left);
            int step = Math.Max(1, span / BarSamplesPerAxis);

            for (int x = left; x <= right && count < BarSamplesPerAxis; x += step)
            {
                for (int s = 0; s < surfaces.Length; s++)
                {
                    ref readonly CapturedSurface surface = ref surfaces[s];
                    if (!surface.Contains(x, y))
                    {
                        continue;
                    }

                    byte* pixel = surface.Pixel(x - surface.X, y - surface.Y);
                    sum += (pixel[0] + pixel[1] + pixel[2]) / 3.0;
                    count++;
                    break;
                }
            }

            return count == 0 ? 0.0 : sum / count / 255.0;
        }

        private static unsafe double ColumnLuma(CapturedSurface[] surfaces, int x, int top, int bottom)
        {
            double sum = 0.0;
            int count = 0;
            int span = Math.Max(1, bottom - top);
            int step = Math.Max(1, span / BarSamplesPerAxis);

            for (int y = top; y <= bottom && count < BarSamplesPerAxis; y += step)
            {
                for (int s = 0; s < surfaces.Length; s++)
                {
                    ref readonly CapturedSurface surface = ref surfaces[s];
                    if (!surface.Contains(x, y))
                    {
                        continue;
                    }

                    byte* pixel = surface.Pixel(x - surface.X, y - surface.Y);
                    sum += (pixel[0] + pixel[1] + pixel[2]) / 3.0;
                    count++;
                    break;
                }
            }

            return count == 0 ? 0.0 : sum / count / 255.0;
        }

        private static unsafe CaptureRegion ApplyCinematicBars(CapturedSurface[] surfaces, CaptureRegion region, double threshold)
        {
            if (region.Width < MinDisplayExtent * 2 || region.Height < MinDisplayExtent * 2)
            {
                return region;
            }

            threshold = Math.Clamp(threshold, 0.0, 0.5);
            int maxTop = (int)Math.Floor(region.Height * MaxBarFraction);
            int maxSide = (int)Math.Floor(region.Width * MaxBarFraction);

            int top = 0;
            while (top < maxTop && RowLuma(surfaces, region.Y + top, region.X, region.Right - 1) <= threshold)
            {
                top++;
            }

            int bottom = 0;
            while (bottom < maxTop && RowLuma(surfaces, region.Bottom - 1 - bottom, region.X, region.Right - 1) <= threshold)
            {
                bottom++;
            }

            int left = 0;
            while (left < maxSide && ColumnLuma(surfaces, region.X + left, region.Y, region.Bottom - 1) <= threshold)
            {
                left++;
            }

            int right = 0;
            while (right < maxSide && ColumnLuma(surfaces, region.Right - 1 - right, region.Y, region.Bottom - 1) <= threshold)
            {
                right++;
            }

            CaptureRegion inset = region.Inset(left, top, right, bottom);
            if (inset.Width < MinDisplayExtent || inset.Height < MinDisplayExtent)
            {
                return region;
            }

            return inset;
        }

        private static CaptureRegion Union(CapturedSurface[] surfaces)
        {
            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;

            foreach (CapturedSurface surface in surfaces)
            {
                left = Math.Min(left, surface.X);
                top = Math.Min(top, surface.Y);
                right = Math.Max(right, surface.Right);
                bottom = Math.Max(bottom, surface.Bottom);
            }

            return new CaptureRegion(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
    }
}