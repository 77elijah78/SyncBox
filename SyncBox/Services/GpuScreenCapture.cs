using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SyncBox.Services
{
    public sealed unsafe class GpuScreenCapture : IScreenSource
    {
        private const int DxgiErrorAccessLost = unchecked((int)0x887A0005);
        private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
        private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0007);
        private const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0020);
        private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

        private sealed class OutputTarget : IDisposable
        {
            public IDXGIOutput1? Output;
            public IDXGIOutputDuplication? Duplication;
            public ID3D11Texture2D? Staging;
            public MappedSubresource Map;
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public bool Mapped;

            public void Dispose()
            {
                Duplication?.Dispose();
                Duplication = null;
                Staging?.Dispose();
                Staging = null;
                Output?.Dispose();
                Output = null;
                Mapped = false;
            }
        }

        private readonly List<OutputTarget> _targets = new();
        private readonly List<CapturedSurface> _pending = new();
        private readonly uint _timeoutMs;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private CapturedSurface[] _surfaces = Array.Empty<CapturedSurface>();
        private int _recoveries;
        private bool _disposed;

        public GpuScreenCapture(uint timeoutMs = 8)
        {
            _timeoutMs = timeoutMs;
            CreateDevice();
        }

        public string Name => "GPU (DXGI Desktop Duplication)";

        public string? LastError { get; private set; }

        public int RecoveryCount => _recoveries;

        public int OutputCount => _targets.Count;

        public int OriginX { get; private set; }

        public int OriginY { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool TryBeginFrame(out CapturedSurface[] surfaces)
        {
            surfaces = Array.Empty<CapturedSurface>();
            if (_disposed || _targets.Count == 0)
            {
                LastError = "No capturable DXGI output was found.";
                return false;
            }

            try
            {
                bool any = false;
                bool mustRecover = false;
                string? failure = null;

                foreach (OutputTarget target in _targets)
                {
                    if (target.Duplication is null || target.Staging is null)
                    {
                        continue;
                    }

                    var result = target.Duplication.AcquireNextFrame((int)_timeoutMs, out _, out IDXGIResource? resource);
                    int code = result.Code;

                    if (result.Failure || resource is null)
                    {
                        if (code == DxgiErrorWaitTimeout || code == DxgiErrorNotCurrentlyAvailable)
                        {
                            continue;
                        }

                        if (code is DxgiErrorAccessLost or DxgiErrorDeviceHung or DxgiErrorDeviceRemoved)
                        {
                            mustRecover = true;
                            failure = $"Desktop duplication reported 0x{code:X8}.";
                            break;
                        }

                        continue;
                    }

                    using (resource)
                    {
                        using ID3D11Texture2D? texture = resource.QueryInterface<ID3D11Texture2D>();
                        if (texture is null)
                        {
                            mustRecover = true;
                            failure = "Acquired frame did not expose a D3D11 texture.";
                            break;
                        }

                        _context!.CopyResource(target.Staging, texture);
                    }

                    target.Duplication.ReleaseFrame();
                    target.Mapped = true;
                    any = true;
                }

                if (mustRecover)
                {
                    UnmapAll();
                    _recoveries++;
                    LastError = (failure ?? "Desktop duplication was invalidated.") + " Recreating the capture device.";
                    return TryRecreate();
                }

                if (!any)
                {
                    LastError = null;
                    return false;
                }

                _pending.Clear();
                foreach (OutputTarget target in _targets)
                {
                    if (!target.Mapped || target.Staging is null || _context is null)
                    {
                        continue;
                    }

                    target.Map = _context!.Map(target.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    _pending.Add(new CapturedSurface(
                        (byte*)target.Map.DataPointer,
                        (int)target.Map.RowPitch,
                        target.X,
                        target.Y,
                        target.Width,
                        target.Height));
                }

                if (_pending.Count == 0)
                {
                    LastError = "Desktop duplication returned no readable surface.";
                    return false;
                }

                if (_surfaces.Length != _pending.Count)
                {
                    _surfaces = new CapturedSurface[_pending.Count];
                }

                _pending.CopyTo(_surfaces);
                LastError = null;
                surfaces = _surfaces;
                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NullReferenceException or ArgumentException)
            {
                UnmapAll();
                _recoveries++;
                LastError = "Desktop duplication failed (" + ex.Message + "). Recreating the capture device.";
                return TryRecreate();
            }
        }

        public void EndFrame() => UnmapAll();

        private bool TryRecreate()
        {
            ReleaseTargets();
            try
            {
                CreateDevice();
                return false;
            }
            catch (Exception ex)
            {
                LastError = "Could not recreate the GPU capture device: " + ex.Message;
                return false;
            }
        }

        private void CreateDevice()
        {
            ReleaseTargets();

            var createResult = D3D11.D3D11CreateDevice(
                null!,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null!,
                out ID3D11Device? device,
                out ID3D11DeviceContext? context);

            if (createResult.Failure || device is null || context is null)
            {
                throw new InvalidOperationException($"D3D11CreateDevice failed with 0x{createResult.Code:X8}.");
            }

            _device = device;
            _context = context;

            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;
            int captured = 0;

            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();

            for (uint index = 0; index < 8; index++)
            {
                var enumResult = adapter.EnumOutputs((int)index, out IDXGIOutput? output);
                if (enumResult.Failure || output is null)
                {
                    break;
                }

                using (output)
                {
                    try
                    {
                        IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                        var desc = output.Description;
                        int x = desc.DesktopCoordinates.Left;
                        int y = desc.DesktopCoordinates.Top;
                        int w = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                        int h = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                        if (w <= 0 || h <= 0)
                        {
                            output1.Dispose();
                            continue;
                        }

                        IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);
                        if (duplication is null)
                        {
                            output1.Dispose();
                            continue;
                        }

                        ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = w,
                            Height = h,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            BindFlags = BindFlags.None
                        });

                        _targets.Add(new OutputTarget
                        {
                            Output = output1,
                            Duplication = duplication,
                            Staging = staging,
                            X = x,
                            Y = y,
                            Width = w,
                            Height = h
                        });

                        captured++;

                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x + w);
                        bottom = Math.Max(bottom, y + h);
                    }
                    catch
                    {
                        captured++;
                    }
                }
            }

            if (captured == 0)
            {
                throw new InvalidOperationException("The GPU reported no outputs that support desktop duplication.");
            }

            OriginX = left;
            OriginY = top;
            Width = right - left;
            Height = bottom - top;
            LastError = null;
        }

        private void UnmapAll()
        {
            foreach (OutputTarget target in _targets)
            {
                if (!target.Mapped)
                {
                    continue;
                }

                target.Mapped = false;
                try
                {
                    _context?.Unmap(target.Staging, 0);
                }
                catch
                {
                }
            }

            _pending.Clear();
        }

        private void ReleaseTargets()
        {
            UnmapAll();
            foreach (OutputTarget target in _targets)
            {
                target.Dispose();
            }

            _targets.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseTargets();
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _surfaces = Array.Empty<CapturedSurface>();
        }
    }
}