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
        private static readonly TimeSpan WifiScanBudget = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan WifiJoinBudget = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan LanScanBudget = TimeSpan.FromSeconds(60);

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
        private bool _initialized;
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
        private bool autoDetectWifi = true;

        [ObservableProperty]
        private bool autoFindOnNetwork = true;

        [ObservableProperty]
        private string wifiPassword = string.Empty;

        [ObservableProperty]
        private string wifiStatusText = "Scan to list the Wi-Fi networks in range.";

        [ObservableProperty]
        private WifiNetwork? selectedWifiNetwork;

        [ObservableProperty]
        private string networkStatusText = "Scan to list the devices connected to your router.";

        [ObservableProperty]
        private LanDevice? selectedLanDevice;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DetectDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(ScanWifiCommand))]
        [NotifyCanExecuteChangedFor(nameof(FindNetworkDevicesCommand))]
        private bool isNetworkBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(DetectDeviceCommand))]
        [NotifyCanExecuteChangedFor(nameof(ScanWifiCommand))]
        [NotifyCanExecuteChangedFor(nameof(FindNetworkDevicesCommand))]
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

            LanDevices = new ObservableCollection<LanDevice>();
            AvailableWifiNetworks = new ObservableCollection<WifiNetwork>();
            ActiveWindows = new ObservableCollection<WindowInfo>();
            LedPreviewItems = new ObservableCollection<LedPreviewItem>();

            ApplySettings(_settingsService.Load());
            _sampler.Configure(HorizontalLedCount, VerticalLedCount);
            RebuildPreview();
            RefreshWindows();
            _initialized = true;

            // Populate the network list before the user presses Connect, exactly like the
            // serial port list is filled at startup. Only when a UI synchronization
            // context exists, so the awaited scan resumes on the thread that owns the
            // bound properties (a view model built on a bare thread has none).
            if (UseWifi && SynchronizationContext.Current is not null)
            {
                if (AutoFindOnNetwork)
                {
                    _ = FindNetworkDevicesCommand.ExecuteAsync(null);
                }

                if (AutoDetectWifi)
                {
                    _ = ScanWifiAsync();
                }
            }
        }

        public ObservableCollection<LanDevice> LanDevices { get; }

        public ObservableCollection<WifiNetwork> AvailableWifiNetworks { get; }

        public ObservableCollection<WindowInfo> ActiveWindows { get; }

        public ObservableCollection<LedPreviewItem> LedPreviewItems { get; }

        public bool HasWifiNetworks => AvailableWifiNetworks.Count > 0;

        public bool HasLanDevices => LanDevices.Count > 0;

        public int TotalLedCount => (HorizontalLedCount + VerticalLedCount) * 2;

        public int FrameByteSize => LedProtocol.FrameSize(TotalLedCount);

        public string SettingsPath => _settingsService.FilePath;

        public string ConnectButtonText => IsConnected ? "DISCONNECT" : "CONNECT";

        public string TargetFpsText => TargetFps <= 0 ? "Unlimited" : TargetFps + " FPS";

        public string BaudRateText => LedLimits.FixedBaudRate.ToString("N0") + " baud (fixed)";

        public string DetectedPortText => string.IsNullOrWhiteSpace(ComPort) ? "Not detected yet" : ComPort;

        public bool CanStart() => IsConnected && !IsStreaming && !IsBusy;

        public bool CanStop() => IsStreaming;

        public bool CanScanWifi() => !IsNetworkBusy && !IsDetecting;

        public bool CanFindNetworkDevices() => !IsNetworkBusy && !IsDetecting;

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

        partial void OnIsCinematicModeChanged(bool value) => _render.Cinematic = value;

        partial void OnComPortChanged(string value) => OnPropertyChanged(nameof(DetectedPortText));

        partial void OnSelectedWindowChanged(WindowInfo? value) => _sampler.RequestReset();

        partial void OnWifiPasswordChanged(string value)
        {
            if (value.Length > 128)
            {
                WifiPassword = value[..128];
            }
        }

        partial void OnSelectedLanDeviceChanged(LanDevice? value)
        {
            if (value is not null)
            {
                IpAddress = value.AddressText;
            }
        }

        partial void OnUseWifiChanged(bool value)
        {
            if (value && RequireAck)
            {
                RequireAck = false;
                Message("UDP has no acknowledgement channel, so ACK waiting was disabled.");
            }

            if (!value || !_initialized || IsConnected || IsNetworkBusy || IsDetecting)
            {
                return;
            }

            if (AutoFindOnNetwork)
            {
                _ = FindNetworkDevicesCommand.ExecuteAsync(null);
            }

            if (AutoDetectWifi)
            {
                _ = ScanWifiAsync();
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
            AutoDetectWifi = settings.AutoDetectWifi;
            AutoFindOnNetwork = settings.AutoFindOnNetwork;
            WifiPassword = settings.WifiPassword;
            HorizontalLedCount = LedLimits.Clamp(settings.HorizontalLedCount);
            VerticalLedCount = LedLimits.Clamp(settings.VerticalLedCount);
            ComPort = settings.ComPort;
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
            AutoDetectWifi = AutoDetectWifi,
            AutoFindOnNetwork = AutoFindOnNetwork,
            WifiPassword = WifiPassword,
            HorizontalLedCount = HorizontalLedCount,
            VerticalLedCount = VerticalLedCount,
            ComPort = ComPort,
            BaudRate = LedLimits.FixedBaudRate,
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

        public bool CanConnect() => !IsDetecting && !IsNetworkBusy;

        private async Task ConnectUdpAsync()
        {
            string? networkNote = null;

            if (networkNote is null && AutoFindOnNetwork)
            {
                LanScanResult scan = await ScanNetworkAsync();
                if (scan.Scanned && scan.Devices.Count == 0)
                {
                    Message(scan.Summary + " Falling back to the configured controller address.");
                }
                else
                {
                    LanDevice? device = LanDiscovery.ChooseControllerDevice(
                        scan.Devices,
                        SelectedLanDevice?.AddressText ?? IpAddress);

                    if (device is not null)
                    {
                        SelectedLanDevice = device;
                        IpAddress = device.AddressText;
                        networkNote = $"Connected to the controller found on the network: {device.DisplayName}.";
                    }
                    else if (scan.Devices.Count > 0)
                    {
                        Message(scan.Summary + " Falling back to the configured controller address.");
                    }
                }
            }

            if (networkNote is null && AutoDetectWifi)
            {
                // Nothing on the router answered: the controller may be running its own
                // access point, so join the selected network when it is in range.
                WifiJoinOutcome outcome = await JoinControllerWifiAsync();
                if (outcome.Joined)
                {
                    networkNote = outcome.Summary;
                    if (!string.IsNullOrWhiteSpace(outcome.ControllerAddress))
                    {
                        IpAddress = outcome.ControllerAddress!;
                    }
                }
                else
                {
                    Message(outcome.Summary + " Falling back to the configured controller address.");
                }
            }

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

            string connectionNote = networkNote is null
                ? $"UDP is connectionless: the controller is assumed to listen on {IpAddress}:{Port}."
                : $"{networkNote} UDP is connectionless: the controller is assumed to listen on {IpAddress}:{Port}.";
            FinishConnect(transport, connectionNote);
        }

        [RelayCommand(CanExecute = nameof(CanFindNetworkDevices))]
        private async Task FindNetworkDevicesAsync()
        {
            LanScanResult result = await ScanNetworkAsync();
            Message(result.Summary);
        }

        /// <summary>
        /// Lists the devices the router has handed an address to and highlights the one
        /// that identifies as a SyncBox controller. Nothing is connected here.
        /// </summary>
        private async Task<LanScanResult> ScanNetworkAsync()
        {
            if (IsNetworkBusy || IsDetecting)
            {
                return new LanScanResult(
                    false,
                    "Another scan is already running.",
                    Array.Empty<LanDevice>(),
                    Array.Empty<string>());
            }

            IsNetworkBusy = true;
            NetworkStatusText = "Looking for devices on your network...";
            int port = Port;

            try
            {
                using var budget = new CancellationTokenSource(LanScanBudget);
                var progress = new Progress<string>(text => NetworkStatusText = text);
                LanScanResult result = await Task.Run(
                    () => LanDiscovery.Scan(port, progress, budget.Token));

                ApplyLanDevices(result.Devices);
                NetworkStatusText = result.Summary;
                return result;
            }
            catch (Exception ex)
            {
                NetworkStatusText = "The network scan failed: " + ex.Message;
                return new LanScanResult(false, NetworkStatusText, Array.Empty<LanDevice>(), Array.Empty<string>());
            }
            finally
            {
                IsNetworkBusy = false;
            }
        }

        private void ApplyLanDevices(IReadOnlyList<LanDevice> devices)
        {
            string? previous = SelectedLanDevice?.AddressText;

            LanDevices.Clear();
            foreach (LanDevice device in devices)
            {
                LanDevices.Add(device);
            }

            LanDevice? selection = previous is null
                ? null
                : LanDevices.FirstOrDefault(device =>
                    string.Equals(device.AddressText, previous, StringComparison.OrdinalIgnoreCase));

            SelectedLanDevice = selection ?? LanDiscovery.ChooseControllerDevice(LanDevices, null);
            OnPropertyChanged(nameof(HasLanDevices));
        }

        [RelayCommand(CanExecute = nameof(CanScanWifi))]
        private async Task ScanWifiAsync()
        {
            if (IsNetworkBusy || IsDetecting)
            {
                return;
            }

            IsNetworkBusy = true;
            WifiStatusText = "Scanning the Wi-Fi networks in range...";
            try
            {
                using var budget = new CancellationTokenSource(WifiScanBudget);
                WifiScanResult result = await Task.Run(() => WifiController.Scan(null, budget.Token));
                ApplyWifiNetworks(result.Networks);
                WifiStatusText = result.Summary;
                Message(result.Summary);
            }
            catch (Exception ex)
            {
                WifiStatusText = "The Wi-Fi scan failed: " + ex.Message;
                Message(WifiStatusText, isError: true);
            }
            finally
            {
                IsNetworkBusy = false;
            }
        }

        private async Task<WifiJoinOutcome> JoinControllerWifiAsync()
        {
            if (IsNetworkBusy)
            {
                return WifiJoinOutcome.Failed("Another Wi-Fi operation is already running.", Array.Empty<WifiNetwork>());
            }

            IsNetworkBusy = true;
            WifiStatusText = "Searching for the SyncBox Wi-Fi network...";
            string? preferredSsid = SelectedWifiNetwork?.Ssid;
            string password = WifiPassword;

            try
            {
                using var budget = new CancellationTokenSource(WifiJoinBudget);
                var progress = new Progress<string>(text => WifiStatusText = text);
                WifiJoinOutcome outcome = await Task.Run(
                    () => WifiController.JoinController(preferredSsid, password, progress, budget.Token));

                ApplyWifiNetworks(outcome.Networks);
                WifiStatusText = outcome.Summary;
                return outcome;
            }
            catch (Exception ex)
            {
                WifiStatusText = "The Wi-Fi connection failed: " + ex.Message;
                return WifiJoinOutcome.Failed(WifiStatusText, Array.Empty<WifiNetwork>());
            }
            finally
            {
                IsNetworkBusy = false;
            }
        }

        private void ApplyWifiNetworks(IReadOnlyList<WifiNetwork> networks)
        {
            // Every network in range is listed so the names are actually visible; the
            // SyncBox ones are sorted to the top and marked by WifiDiscovery.
            IReadOnlyList<WifiNetwork> ordered = WifiDiscovery.SortForDisplay(networks);
            string? previous = SelectedWifiNetwork?.Ssid;

            AvailableWifiNetworks.Clear();
            foreach (WifiNetwork network in ordered)
            {
                AvailableWifiNetworks.Add(network);
            }

            WifiNetwork? selection = previous is null
                ? null
                : AvailableWifiNetworks.FirstOrDefault(network =>
                    string.Equals(network.Ssid, previous, StringComparison.OrdinalIgnoreCase));

            SelectedWifiNetwork = selection
                ?? AvailableWifiNetworks.FirstOrDefault(network => network.LooksLikeController)
                ?? AvailableWifiNetworks.FirstOrDefault();

            OnPropertyChanged(nameof(HasWifiNetworks));
        }

        /// <summary>
        /// The port is never chosen by hand: connecting runs the same handshake as
        /// Detect, so the controller is identified (and remembered) automatically.
        /// </summary>
        private async Task ConnectSerialAsync()
        {
            DetectionOutcome outcome = await DetectControllerAsync();
            if (!outcome.Found)
            {
                Message(outcome.Summary, isError: true);
                StatusText = "No controller";
                return;
            }

            SyncBoxDevice device = outcome.Device!;
            ComPort = device.PortName;
            DeviceVersionText = $"{device.FirmwareVersion} on {device.PortName} @ {device.BaudRate}";

            ISyncTransport transport;
            try
            {
                transport = new SerialTransport(device.PortName, LedLimits.FixedBaudRate);
                transport.Open();
            }
            catch (Exception ex)
            {
                Message(ex.Message, isError: true);
                StatusText = "Connect failed";
                return;
            }

            FinishConnect(transport, $"Controller answered the handshake: {device.FirmwareVersion} on {device.PortName}.");
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
                return new DetectionOutcome(false, "No serial ports were found. Plug the controller in and press Detect.");
            }

            IsDetecting = true;
            string? preferred = ComPort;

            // One baud rate only, the one the controller is known to speak.
            IReadOnlyList<int> attempts = new[] { LedLimits.FixedBaudRate };

            Post(() => Message($"Scanning {ports.Count} port(s) at {LedLimits.FixedBaudRate:N0} baud for a Sync Box controller..."));

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
                        ? $"The port scan hit its {ScanBudget.TotalSeconds:0}s budget before a controller answered. Close other apps using the port and try again."
                        : $"No controller answered on {string.Join(", ", ports)} at {LedLimits.FixedBaudRate:N0} baud. Check the cable and that the firmware runs at 1,000,000 baud.");
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