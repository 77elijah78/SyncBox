using System;
using System.Runtime.InteropServices;

namespace SyncBox.Services
{
    public sealed unsafe class CpuScreenCapture : IScreenSource
    {
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const uint BI_RGB = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("user32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors0;
            public uint bmiColors1;
            public uint bmiColors2;
        }

        private IntPtr _screenDc;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _previousBitmap;
        private byte* _bits;
        private CapturedSurface[] _surfaces = Array.Empty<CapturedSurface>();
        private bool _disposed;

        public CpuScreenCapture()
        {
            OriginX = GetSystemMetrics(76);
            OriginY = GetSystemMetrics(77);
            Width = Math.Max(1, GetSystemMetrics(78));
            Height = Math.Max(1, GetSystemMetrics(79));

            _screenDc = GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not obtain a device context for the desktop.");
            }

            _memoryDc = CreateCompatibleDC(_screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
                throw new InvalidOperationException("Could not create an off-screen device context.");
            }

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = Width,
                    biHeight = -Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                    biSizeImage = (uint)(Width * Height * 4)
                }
            };

            IntPtr bits;
            _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                DeleteDC(_memoryDc);
                ReleaseDC(IntPtr.Zero, _screenDc);
                _memoryDc = IntPtr.Zero;
                _screenDc = IntPtr.Zero;
                throw new InvalidOperationException("Could not allocate a 32-bit capture buffer.");
            }

            _previousBitmap = SelectObject(_memoryDc, _bitmap);
            _bits = (byte*)bits;
            _surfaces = new[] { new CapturedSurface(_bits, Width * 4, OriginX, OriginY, Width, Height) };
        }

        public string Name => "CPU (GDI BitBlt)";

        public string? LastError { get; private set; }

        public int OriginX { get; }

        public int OriginY { get; }

        public int Width { get; }

        public int Height { get; }

        public bool TryBeginFrame(out CapturedSurface[] surfaces)
        {
            surfaces = Array.Empty<CapturedSurface>();
            if (_disposed || _memoryDc == IntPtr.Zero)
            {
                LastError = "Capture buffer is not available.";
                return false;
            }

            bool copied = BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, OriginX, OriginY, SRCCOPY | CAPTUREBLT);
            if (!copied)
            {
                LastError = "BitBlt of the desktop failed (error " + Marshal.GetLastWin32Error() + ").";
                return false;
            }

            LastError = null;
            surfaces = _surfaces;
            return true;
        }

        public void EndFrame()
        {
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
            {
                SelectObject(_memoryDc, _previousBitmap);
            }

            if (_bitmap != IntPtr.Zero)
            {
                DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }

            if (_memoryDc != IntPtr.Zero)
            {
                DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }

            if (_screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }

            _bits = null;
            _surfaces = Array.Empty<CapturedSurface>();
        }
    }
}