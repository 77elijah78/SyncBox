using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncBox.Services;

namespace SyncBox.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private const int AckTimeoutMs = 12;
        private const int MaxConsecutiveAckMisses = 8;
        private const long FpsReportIntervalMs = 500;
        private const long PreviewIntervalMs = 50;
        private const long NoteIntervalMs = 2000;
        private const int CaptureRetryDelayMs = 5;
        private static readonly TimeSpan ScanProbeTimeout = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(10);

        private readonly SettingsService _settingsService;
        private readonly LedSampler _sampler = new();
        private readonly LedRenderSettings _render = new();
        private readonly Stopwatch _frameClock = new();
        private readonly int _ownProcessId;

        private SemaphoreSlim _ackSignal = new(0, 1);
        private ISyncTransport? _transport;
        private CancellationTokenSource? _cancellation;
        private Task? _loop;
        private bool _disposed;
        private long _lastNoteMs;
        private bool _ackAutoDisabled;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
        private bool isConnected;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        private bool isStreaming;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        private bool isBusy;

        [ObservableProperty]
        private bool useGpu = true;

        [ObservableProperty]
        private bool useWifi;

        [ObservableProperty]
        private bool captureWholeDesktop = true;

        [ObservableProperty]
        private bool isCinematicMode = true;

        [ObservableProperty]
        private bool requireAck;

        [ObservableProperty]
        private bool autoDetectDevice = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DetectDeviceCommand))]
        private bool isDetecting;

        [ObservableProperty]
        private string deviceVersionText = "No controller detected yet";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalLedCount))]
        [NotifyPropertyChangedFor(nameof(FrameByteSize))]
        private int horizontalLedCount = 19;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalLedCount))]
        [NotifyPropertyChangedFor(nameof(FrameByteSize))]
        private int verticalLedCount = 10;

        [ObservableProperty]
        private string comPort = string.Empty;

        [ObservableProperty]
        private int selectedBaud = 1000000;

        [ObservableProperty]
        private string ipAddress = "192.168.4.1";

        [ObservableProperty]
        private int port = 1234;

        [ObservableProperty]
        private double globalBrightness = 0.85;

        [ObservableProperty]
        private double gamma = 1.0;

        [ObservableProperty]
        private double saturation = 1.35;

        [ObservableProperty]
        private double smoothing = 0.7;

        [ObservableProperty]
        private double barThreshold = 0.06;

        [ObservableProperty]
        private int averageSize = 3;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TargetFpsText))]
        private int targetFps = 60;

        [ObservableProperty]
        private int fps;

        [ObservableProperty]
        private string statusText = "Ready";

        [ObservableProperty]
        private string messageText = string.Empty;

        [ObservableProperty]
        private bool hasError;

        [ObservableProperty]
        private string captureInfo = string.Empty;

        [ObservableProperty]
        private string connectionSummary = "Disconnected";

        [ObservableProperty]
        private WindowInfo? selectedWindow;

        public MainViewModel()
            : this(new SettingsService())
        {
        }

        public MainViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService;
            _ownProcessId = Environment.ProcessId;

            BaudRates = new ObservableCollection<int>
            {
                9600, 19200, 38400, 57600, 115200, 230400, 460800, 500000, 921600, 1000000, 1500000, 2000000
            };
            AvailablePorts = new ObservableCollection<string>();
            ActiveWindows = new ObservableCollection<WindowInfo>();
            LedPreviewItems = new ObservableCollection<LedPreviewItem>();

            ApplySettings(_settingsService.Load());
            _sampler.Configure(HorizontalLedCount, VerticalLedCount);
            RebuildPreview();
            SyncPortList();
            RefreshWindows();
        }

        public ObservableCollection<int> BaudRates { get; }

        public ObservableCollection<string> AvailablePorts { get; }

        public ObservableCollection<WindowInfo> ActiveWindows { get; }

        public ObservableCollection<LedPreviewItem> LedPreviewItems { get; }

        public int TotalLedCount => (HorizontalLedCount + VerticalLedCount) * 2;

        public int FrameByteSize => LedProtocol.FrameSize(TotalLedCount);

        public string SettingsPath => _settingsService.FilePath;

        public string ConnectButtonText => IsConnected ? "DISCONNECT" : "CONNECT";

        public string TargetFpsText => TargetFps <= 0 ? "Unlimited" : TargetFps + " FPS";

        public bool CanStart() => IsConnected && !IsStreaming && !IsBusy;

        public bool CanStop() => IsStreaming;

        partial void OnHorizontalLedCountChanged(int value)
        {
            int clamped = LedLimits.Clamp(value);
            if (clamped != value)
            {
                HorizontalLedCount = clamped;
                return;
            }

            OnLayoutChanged();
        }

        partial void OnVerticalLedCountChanged(int value)
        {
            int clamped = LedLimits.Clamp(value);
            if (clamped != value)
            {
                VerticalLedCount = clamped;
                return;
            }

            OnLayoutChanged();
        }

        partial void OnGlobalBrightnessChanged(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            _render.Brightness = clamped;
            if (clamped != value)
            {
                GlobalBrightness = clamped;
            }
        }

        partial void OnGammaChanged(double value)
        {
            double clamped = Math.Clamp(value, 0.2, 5.0);
            _render.Gamma = clamped;
            if (clamped != value)
            {
                Gamma = clamped;
            }
        }

        partial void OnSaturationChanged(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 3.0);
            _render.Saturation = clamped;
            if (clamped != value)
            {
                Saturation = clamped;
            }
        }

        partial void OnSmoothingChanged(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 0.98);
            _render.Smoothing = clamped;
            if (clamped != value)
            {
                Smoothing = clamped;
            }
        }

        partial void OnBarThresholdChanged(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 0.5);
            _render.BarThreshold = clamped;
            if (clamped != value)
            {
                BarThreshold = clamped;
            }
        }

        partial void OnAverageSizeChanged(int value)
        {
            int clamped = Math.Clamp(value, 1, 5);
            _render.AverageSize = clamped;
            if (clamped != value)
            {
                AverageSize = clamped;
            }
        }

        partial void OnTargetFpsChanged(int value)
        {
            int clamped = Math.Clamp(value, 0, 240);
            if (clamped != value)
            {
                TargetFps = clamped;
            }
        }

        partial void OnPortChanged(int value)
        {
            int clamped = LedLimits.IsValidPort(value) ? value : Math.Clamp(value, 1, 65535);
            if (clamped != value)
            {
                Port = clamped;
            }
        }

        partial void OnSelectedBaudChanged(int value)
        {
            int clamped = LedLimits.ClampBaud(value);
            if (clamped != value)
            {
                SelectedBaud = clamped;
            }
        }

        partial void OnIsCinematicModeChanged(bool value) => _render.Cinematic = value;

        partial void OnSelectedWindowChanged(WindowInfo? value) => _sampler.RequestReset();

        partial void OnUseWifiChanged(bool value)
        {
            if (value && RequireAck)
            {
                RequireAck = false;
                Message("UDP has no acknowledgement channel, so ACK waiting was disabled.");
            }
        }

        private void OnLayoutChanged()
        {
            _sampler.RequestReset();
            RebuildPreview();

            if (IsStreaming)
            {
                Message($"Layout is now {TotalLedCount} LEDs. The controller must match this layout.");
            }
        }

        private void ApplySettings(AppSettings settings)
        {
            UseGpu = settings.UseGpu;
            UseWifi = settings.UseWifi;
            CaptureWholeDesktop = settings.CaptureWholeDesktop;
            IsCinematicMode = settings.IsCinematicMode;
            RequireAck = settings.RequireAck;
            AutoDetectDevice = settings.AutoDetectDevice;
            HorizontalLedCount = LedLimits.Clamp(settings.HorizontalLedCount);
            VerticalLedCount = LedLimits.Clamp(settings.VerticalLedCount);
            ComPort = settings.ComPort;
            SelectedBaud = LedLimits.ClampBaud(settings.BaudRate);
            IpAddress = settings.IpAddress;
            Port = LedLimits.IsValidPort(settings.Port) ? settings.Port : 1234;
            GlobalBrightness = Math.Clamp(settings.Brightness, 0.0, 1.0);
            Gamma = Math.Clamp(settings.Gamma, 0.2, 5.0);
            Saturation = Math.Clamp(settings.Saturation, 0.0, 3.0);
            Smoothing = Math.Clamp(settings.Smoothing, 0.0, 0.98);
            BarThreshold = Math.Clamp(settings.BarThreshold, 0.0, 0.5);
            AverageSize = Math.Clamp(settings.AverageSize, 1, 5);
            TargetFps = Math.Clamp(settings.TargetFps, 0, 240);
        }

        private AppSettings CaptureSettings() => new()
        {
            UseGpu = UseGpu,
            UseWifi = UseWifi,
            CaptureWholeDesktop = CaptureWholeDesktop,
            IsCinematicMode = IsCinematicMode,
            RequireAck = RequireAck,
            AutoDetectDevice = AutoDetectDevice,
            HorizontalLedCount = HorizontalLedCount,
            VerticalLedCount = VerticalLedCount,
            ComPort = ComPort,
            BaudRate = SelectedBaud,
            IpAddress = IpAddress,
            Port = Port,
            Brightness = GlobalBrightness,
            Gamma = Gamma,
            Saturation = Saturation,
            Smoothing = Smoothing,
            BarThreshold = BarThreshold,
            AverageSize = AverageSize,
            TargetFps = TargetFps
        };

        private readonly struct DetectionOutcome
        {
            public DetectionOutcome(bool found, string summary, SyncBoxDevice? device = null)
            {
                Found = found;
                Summary = summary;
                Device = device;
            }

            public bool Found { get; }

            public string Summary { get; }

            public SyncBoxDevice? Device { get; }
        }

        [RelayCommand]
        private void RefreshWindows() => _ = Task.Run(RefreshWindowsCore);

        private void RefreshWindowsCore()
        {
            IReadOnlyList<WindowInfo> windows;
            try
            {
                windows = WindowCatalog.Enumerate(_ownProcessId);
            }
            catch (Exception ex)
            {
                Post(() => Message("Could not enumerate windows: " + ex.Message, isError: true));
                return;
            }

            Post(() =>
            {
                WindowInfo? previous = SelectedWindow;
                ActiveWindows.Clear();
                foreach (WindowInfo window in windows)
                {
                    ActiveWindows.Add(window);
                }

                SelectedWindow = previous is not null && ActiveWindows.Contains(previous) ? previous : null;
                Message($"{ActiveWindows.Count} window(s) available.");
            });
        }

        [RelayCommand]
        private void RefreshSerialPorts()
        {
            SyncPortList();
            Message($"{AvailablePorts.Count} serial port(s) detected.");
        }

        private void SyncPortList()
        {
            IReadOnlyList<string> ports = SerialTransport.AvailablePorts();
            Post(() =>
            {
                string current = ComPort;
                AvailablePorts.Clear();
                foreach (string name in ports)
                {
                    AvailablePorts.Add(name);
                }

                if (!string.IsNullOrWhiteSpace(current) && !AvailablePorts.Contains(current))
                {
                    AvailablePorts.Add(current);
                }
            });
        }

        [RelayCommand(CanExecute = nameof(CanConnect))]
        private async Task ToggleConnectionAsync()
        {
            if (IsConnected)
            {
                CancelLoop();
                DisposeTransport();
                IsConnected = false;
                ConnectionSummary = "Disconnected";
                StatusText = "Disconnected";
                SaveSettings();
                return;
            }

            if (UseWifi)
            {
                await ConnectUdpAsync();
                return;
            }

            await ConnectSerialAsync();
        }

        public bool CanConnect() => !IsDetecting;

        private async Task ConnectUdpAsync()
        {
            if (!LedLimits.IsValidIp(IpAddress))
            {
                Message($"'{IpAddress}' is not a valid IP address.", isError: true);
                return;
            }

            ISyncTransport transport;
            try
            {
                transport = new UdpTransport(IpAddress, Port);
                transport.Open();
            }
            catch (Exception ex)
            {
                Message(ex.Message, isError: true);
                StatusText = "Connect failed";
                return;
            }

            FinishConnect(transport, $"UDP is connectionless: the controller is assumed to listen on {IpAddress}:{Port}.");
        }

        private async Task ConnectSerialAsync()
        {
            string? detected = null;
            string version = string.Empty;

            if (AutoDetectDevice)
            {
                DetectionOutcome outcome = await DetectControllerAsync();
                if (!outcome.Found)
                {
                    if (string.IsNullOrWhiteSpace(ComPort))
                    {
                        Message(outcome.Summary, isError: true);
                        StatusText = "No controller";
                        return;
                    }

                    Message(outcome.Summary + " Falling back to the selected port.", isError: true);
                }
                else
                {
                    detected = outcome.Device!.PortName;
                    version = outcome.Device.FirmwareVersion;
                }
            }

            string portName = detected ?? ComPort;
            if (string.IsNullOrWhiteSpace(portName))
            {
                if (AvailablePorts.Count == 1)
                {
                    portName = AvailablePorts[0];
                }
                else
                {
                    Message(AvailablePorts.Count == 0
                        ? "No serial ports were found. Connect the controller and press Detect device."
                        : "Select a serial port before connecting.", isError: true);
                    return;
                }
            }

            if (!string.Equals(portName, ComPort, StringComparison.OrdinalIgnoreCase))
            {
                string previous = ComPort;
                ComPort = portName;
                if (!AvailablePorts.Contains(portName))
                {
                    AvailablePorts.Add(portName);
                }

                Message($"Switched from {previous} to {portName}.");
            }

            ISyncTransport transport;
            try
            {
                transport = new SerialTransport(portName, SelectedBaud);
                transport.Open();
            }
            catch (Exception ex)
            {
                Message(ex.Message, isError: true);
                StatusText = "Connect failed";
                return;
            }

            if (detected is not null)
            {
                DeviceVersionText = $"{version} on {detected}";
            }
            else
            {
                DeviceVersionText = "Unverified device on " + portName;
            }

            FinishConnect(transport, version.Length > 0
                ? $"Controller answered the handshake: {version}."
                : "Serial link open. The controller did not answer the handshake, so the port was not verified.");
        }

        private void FinishConnect(ISyncTransport transport, string note)
        {
            if (transport is SerialTransport serial)
            {
                serial.FrameReceived += OnDeviceFrame;
            }

            DisposeTransport();
            _transport = transport;
            IsConnected = true;
            ConnectionSummary = UseWifi ? transport.Name + " (unverified)" : transport.Name;
            StatusText = "Connected";
            Message(note);
            SaveSettings();
        }

        [RelayCommand(CanExecute = nameof(CanConnect))]
        private async Task DetectDeviceAsync()
        {
            DetectionOutcome outcome = await DetectControllerAsync();
            if (!outcome.Found)
            {
                Message(outcome.Summary, isError: true);
                StatusText = "No controller";
                DeviceVersionText = "No Sync Box controller answered";
                return;
            }

            ComPort = outcome.Device!.PortName;
            if (!AvailablePorts.Contains(ComPort))
            {
                AvailablePorts.Add(ComPort);
            }

            DeviceVersionText = $"{outcome.Device.FirmwareVersion} on {outcome.Device.PortName} @ {outcome.Device.BaudRate}";
            Message(outcome.Summary);
        }

        private async Task<DetectionOutcome> DetectControllerAsync()
        {
            if (IsDetecting)
            {
                return new DetectionOutcome(false, "A port scan is already running.");
            }

            IReadOnlyList<string> ports = SerialTransport.AvailablePorts();
            if (ports.Count == 0)
            {
                return new DetectionOutcome(false, "No serial ports were found. Plug the controller in and press Refresh ports.");
            }

            IsDetecting = true;
            string? preferred = ComPort;
            int baud = SelectedBaud;
            var attempts = new List<int> { baud };
            foreach (int fallback in DeviceDiscovery.DefaultBaudRates)
            {
                if (!attempts.Contains(fallback))
                {
                    attempts.Add(fallback);
                }
            }

            Post(() => Message($"Scanning {ports.Count} port(s) for a Sync Box controller..."));

            using var budget = new CancellationTokenSource(ScanBudget);
            try
            {
                IReadOnlyList<SyncBoxDevice> found = await Task.Run(() => DeviceDiscovery.Scan(
                    ports,
                    attempts,
                    preferred,
                    ScanProbeTimeout,
                    budget.Token));

                if (found.Count == 0)
                {
                    return new DetectionOutcome(false, budget.IsCancellationRequested
                        ? $"The port scan hit its {ScanBudget.TotalSeconds:0}s budget before a controller answered. Close other apps using the port, or pick it manually."
                        : $"No controller answered on {string.Join(", ", ports)}. Check the cable and baud rate.");
                }

                SyncBoxDevice device = found[0];
                return new DetectionOutcome(true,
                    found.Count == 1
                        ? $"Found {device.FirmwareVersion} on {device.PortName} @ {device.BaudRate}."
                        : $"Found {string.Join(", ", found.Select(d => d.PortName))}; using {device.PortName}.",
                    device);
            }
            catch (Exception ex)
            {
                return new DetectionOutcome(false, "Port detection failed: " + ex.Message);
            }
            finally
            {
                Post(() => IsDetecting = false);
            }
        }

        private void OnDeviceFrame(LedFrame frame)
        {
            if (frame.Kind != LedFrameKind.Ack)
            {
                return;
            }

            try
            {
                _ackSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private void Start()
        {
            if (_transport is null || !_transport.IsOpen)
            {
                Message("Connect a device before starting.", isError: true);
                return;
            }

            if (IsStreaming || IsBusy)
            {
                return;
            }

            if (RequireAck && UseWifi)
            {
                RequireAck = false;
                Message("UDP cannot acknowledge frames, so ACK waiting was turned off.", isError: true);
            }

            _ackAutoDisabled = false;
            SwapAckSignal();
            _sampler.Configure(HorizontalLedCount, VerticalLedCount);
            _sampler.RequestReset();

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            IsBusy = true;
            IsStreaming = true;
            StatusText = "Streaming";
            Fps = 0;
            _lastNoteMs = 0;
            _frameClock.Restart();
            _loop = Task.Run(() => RunCaptureLoop(cancellation.Token), cancellation.Token);
        }

        [RelayCommand(CanExecute = nameof(CanStop))]
        private void Stop() => CancelLoop();

        private void CancelLoop()
        {
            CancellationTokenSource? cancellation = _cancellation;
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            StatusText = "Stopping";
        }

        private void SwapAckSignal() => _ackSignal = new SemaphoreSlim(0, 1);

        private void RunCaptureLoop(CancellationToken token)
        {
            IScreenSource? source = null;
            ISyncTransport? transport = _transport;

            try
            {
                if (transport is null || !transport.IsOpen)
                {
                    throw new InvalidOperationException("The device connection is closed.");
                }

                if (UseGpu)
                {
                    try
                    {
                        source = new GpuScreenCapture();
                    }
                    catch (Exception ex)
                    {
                        Post(() =>
                        {
                            UseGpu = false;
                            Message($"GPU capture unavailable ({ex.Message}). Falling back to CPU capture.", isError: true);
                        });
                        source = new CpuScreenCapture();
                    }
                }
                else
                {
                    source = new CpuScreenCapture();
                }

                Post(() => CaptureInfo = DescribeSource(source!));

                int ledCount = _sampler.LedCount;
                var pixels = new byte[ledCount * 3];
                var frame = new byte[LedProtocol.FrameSize(ledCount)];
                var settings = _render.Clone();

                int frames = 0;
                int missedAcks = 0;
                long lastReport = 0;
                long lastPreview = 0;
                long nextFrameDueMs = 0;
                bool ackEnabled = RequireAck;
                string? lastTransportError = null;

                while (!token.IsCancellationRequested)
                {
                    int desiredLedCount = (HorizontalLedCount + VerticalLedCount) * 2;
                    if (desiredLedCount != ledCount)
                    {
                        _sampler.Configure(HorizontalLedCount, VerticalLedCount);
                        ledCount = _sampler.LedCount;
                        pixels = new byte[ledCount * 3];
                        frame = new byte[LedProtocol.FrameSize(ledCount)];
                        _sampler.RequestReset();
                        Post(() => Message($"Layout is now {ledCount} LEDs ({frame.Length} bytes per frame)."));
                    }

                    if (RequireAck != ackEnabled)
                    {
                        ackEnabled = RequireAck;
                        missedAcks = 0;
                    }

                    settings.CopyFrom(_render);
                    CaptureRegion region = ResolveRegion();

                    if (source.TryBeginFrame(out CapturedSurface[] surfaces))
                    {
                        if (_sampler.Sample(surfaces, region, settings, pixels))
                        {
                            LedProtocol.WriteFrame(frame, LedFrameKind.Pixels, ledCount, pixels);
                            transport.Send(frame);
                            frames++;

                            string? transportError = transport.LastError;
                            if (!string.IsNullOrEmpty(transportError) &&
                                !string.Equals(transportError, lastTransportError, StringComparison.Ordinal))
                            {
                                lastTransportError = transportError;
                                Post(() => Message(transportError, isError: true));
                            }

                            if (ackEnabled)
                            {
                                if (_ackSignal.Wait(AckTimeoutMs))
                                {
                                    missedAcks = 0;
                                }
                                else if (++missedAcks >= MaxConsecutiveAckMisses && !_ackAutoDisabled)
                                {
                                    _ackAutoDisabled = true;
                                    Post(() =>
                                    {
                                        RequireAck = false;
                                        Message("The controller stopped acknowledging frames, so ACK waiting was turned off.", isError: true);
                                    });
                                }
                            }

                            long clock = _frameClock.ElapsedMilliseconds;
                            if (clock - lastPreview >= PreviewIntervalMs)
                            {
                                lastPreview = clock;
                                byte[] snapshot = (byte[])pixels.Clone();
                                Post(() => UpdatePreview(snapshot));
                            }
                        }

                        source.EndFrame();
                    }
                    else
                    {
                        string? failure = source.LastError;
                        if (!string.IsNullOrEmpty(failure))
                        {
                            Post(() => Message(failure, isError: true));
                        }

                        if (token.WaitHandle.WaitOne(CaptureRetryDelayMs))
                        {
                            break;
                        }
                    }

                    long elapsed = _frameClock.ElapsedMilliseconds;
                    if (elapsed - lastReport >= FpsReportIntervalMs)
                    {
                        int value = frames;
                        frames = 0;
                        lastReport = elapsed;
                        Post(() => Fps = value);
                    }

                    int limit = TargetFps;
                    if (limit > 0)
                    {
                        long interval = 1000 / limit;
                        if (nextFrameDueMs == 0)
                        {
                            nextFrameDueMs = elapsed;
                        }

                        long wait = nextFrameDueMs - elapsed;
                        if (wait > 0)
                        {
                            if (token.WaitHandle.WaitOne((int)Math.Min(wait, 100)))
                            {
                                break;
                            }
                        }
                        else
                        {
                            nextFrameDueMs = elapsed;
                        }

                        nextFrameDueMs += interval;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    Message("Capture stopped: " + ex.Message, isError: true);
                    StatusText = "Faulted";
                });
            }
            finally
            {
                source?.Dispose();

                string? transportError = transport?.LastError;
                if (!string.IsNullOrEmpty(transportError))
                {
                    Post(() => Message(transportError, isError: true));
                }

                Post(() =>
                {
                    IsStreaming = false;
                    IsBusy = false;
                    Fps = 0;
                    StatusText = "Stopped";
                });
            }
        }

        private CaptureRegion ResolveRegion()
        {
            if (CaptureWholeDesktop)
            {
                return default;
            }

            WindowInfo? window = SelectedWindow;
            if (window is not null && WindowCatalog.TryGetVisibleRegion(window, out CaptureRegion region))
            {
                return region;
            }

            ThrottledNote("The selected window is hidden, minimized or closed, so the whole desktop is captured instead.");
            return default;
        }

        private void ThrottledNote(string text)
        {
            long now = _frameClock.ElapsedMilliseconds;
            if (now - _lastNoteMs < NoteIntervalMs)
            {
                return;
            }

            _lastNoteMs = now;
            Post(() => Message(text));
        }

        private static string DescribeSource(IScreenSource source) => source switch
        {
            GpuScreenCapture gpu => $"{gpu.Name} - {gpu.OutputCount} output(s), {gpu.RecoveryCount} recovery event(s)",
            _ => source.Name
        };

        private void RebuildPreview()
        {
            int total = TotalLedCount;
            while (LedPreviewItems.Count > total)
            {
                LedPreviewItems.RemoveAt(LedPreviewItems.Count - 1);
            }

            while (LedPreviewItems.Count < total)
            {
                LedPreviewItems.Add(new LedPreviewItem(LedPreviewItems.Count));
            }
        }

        private void UpdatePreview(byte[] pixels)
        {
            int count = Math.Min(LedPreviewItems.Count, pixels.Length / 3);
            for (int i = 0; i < count; i++)
            {
                LedPreviewItems[i].SetColor(pixels[i * 3], pixels[(i * 3) + 1], pixels[(i * 3) + 2]);
            }
        }

        [RelayCommand]
        private void ResetTuning()
        {
            var defaults = new AppSettings();
            GlobalBrightness = defaults.Brightness;
            Gamma = defaults.Gamma;
            Saturation = defaults.Saturation;
            Smoothing = defaults.Smoothing;
            BarThreshold = defaults.BarThreshold;
            AverageSize = defaults.AverageSize;
            Message("Tuning reset to defaults.");
        }

        [RelayCommand]
        private void ResetSmoothing() => _sampler.RequestReset();

        [RelayCommand]
        private void PersistSettings()
        {
            StatusText = _settingsService.TrySave(CaptureSettings())
                ? "Settings saved."
                : "Settings could not be saved.";
        }

        private void SaveSettings() => _settingsService.TrySave(CaptureSettings());

        private void Message(string text, bool isError = false)
        {
            MessageText = text;
            HasError = isError;
            if (isError && !IsStreaming)
            {
                StatusText = "Error";
            }
        }

        private static void Post(Action action)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.InvokeAsync(action);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelLoop();

            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            DisposeTransport();
            _cancellation?.Dispose();
            _cancellation = null;
            SaveSettings();
        }

        private void DisposeTransport()
        {
            ISyncTransport? transport = _transport;
            _transport = null;
            if (transport is SerialTransport serial)
            {
                serial.FrameReceived -= OnDeviceFrame;
            }

            transport?.Dispose();
        }
    }

    public sealed class LedPreviewItem : ObservableObject
    {
        public LedPreviewItem(int index)
        {
            Index = index;
            Brush = new SolidColorBrush(Colors.Black);
        }

        public int Index { get; }

        public SolidColorBrush Brush { get; }

        public void SetColor(byte red, byte green, byte blue)
        {
            Color color = Color.FromRgb(red, green, blue);
            if (Brush.Color != color)
            {
                Brush.Color = color;
                OnPropertyChanged(nameof(Brush));
            }
        }
    }
}