using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SyncBox.ViewModels;
using Xunit;

namespace SyncBox.Tests
{
    public class UiSmokeTests
    {
        [Fact]
        public void ApplicationStartsAWindowAndTheViewModelStaysValid()
        {
            var failures = new List<string>();
            var bindingErrors = new List<string>();
            string settingsPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "syncbox-tests-" + Guid.NewGuid().ToString("N") + ".json");

            var thread = new Thread(() =>
            {
                var listener = new CapturingListener(bindingErrors);
                Trace.Listeners.Add(listener);
                App? application = null;
                Window? window = null;

                try
                {
                    application = new App();
                    application.InitializeComponent();
                    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    if (application.StartupUri is null ||
                        !application.StartupUri.OriginalString.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add("App.xaml does not declare StartupUri=MainWindow.xaml, so no window would ever open.");
                    }

                    window = new MainWindow();
                    window.Show();
                    PumpUntil(() => window.IsLoaded, TimeSpan.FromSeconds(20));

                    if (!window.IsLoaded)
                    {
                        failures.Add("The window never reached the Loaded state.");
                    }

                    TabControl? tabs = FindDescendant<TabControl>(window);
                    if (tabs is null)
                    {
                        failures.Add("No TabControl was found in the visual tree.");
                    }
                    else if (tabs.Items.Count != 4)
                    {
                        failures.Add($"Expected 4 tabs (dashboard, tuning, LEDs, guide), found {tabs.Items.Count}.");
                    }

                    if (FindDescendant<ScrollBar>(window) is null)
                    {
                        failures.Add("The themed ScrollBar template was not applied.");
                    }

                    if (FindDescendant<Slider>(window) is null)
                    {
                        failures.Add("The themed Slider template was not applied.");
                    }

                    if (FindDescendant<ComboBox>(window) is null)
                    {
                        failures.Add("The themed ComboBox template was not applied.");
                    }

                    if (window.FindName("DetectDeviceButton") is not Button detectButton)
                    {
                        failures.Add("The Detect device button is missing from the interface card.");
                    }
                    else if (detectButton.Command is null)
                    {
                        failures.Add("The Detect device button is not bound to a command.");
                    }

                    if (window.FindName("ScanWifiButton") is not Button scanButton)
                    {
                        failures.Add("The Wi-Fi Scan button is missing from the interface card.");
                    }
                    else if (scanButton.Command is null)
                    {
                        failures.Add("The Wi-Fi Scan button is not bound to a command.");
                    }

                    if (window.FindName("FindNetworkDevicesButton") is not Button findButton)
                    {
                        failures.Add("The network Scan button is missing from the interface card.");
                    }
                    else if (findButton.Command is null)
                    {
                        failures.Add("The network Scan button is not bound to a command.");
                    }

                    if (window.FindName("WifiPasswordBox") is not PasswordBox)
                    {
                        failures.Add("The Wi-Fi password box is missing from the interface card.");
                    }

                    if (window.DataContext is not MainViewModel viewModel)
                    {
                        failures.Add("The data context is not a MainViewModel.");
                    }
                    else
                    {
                        PumpUntil(
                            () => viewModel.MessageText.Contains("window(s) available", StringComparison.Ordinal),
                            TimeSpan.FromSeconds(20));

                        if (!viewModel.MessageText.Contains("window(s) available", StringComparison.Ordinal))
                        {
                            failures.Add("Window enumeration never reported a result. Message: '" + viewModel.MessageText + "'.");
                        }

                        viewModel.HorizontalLedCount = -50;
                        viewModel.VerticalLedCount = 100000;
                        viewModel.Port = 999999;
                        viewModel.GlobalBrightness = 5.0;
                        viewModel.Gamma = -3.0;
                        viewModel.AverageSize = 999;
                        viewModel.TargetFps = 9999;

                        Check(viewModel.HorizontalLedCount == 1, "HorizontalLedCount was not clamped.", failures);
                        Check(viewModel.VerticalLedCount == Services.LedLimits.MaxLedCount, "VerticalLedCount was not clamped.", failures);
                        Check(viewModel.Port == 65535, "Port was not clamped.", failures);
                        Check(viewModel.GlobalBrightness == 1.0, "Brightness was not clamped.", failures);
                        Check(viewModel.Gamma == 0.2, "Gamma was not clamped.", failures);
                        Check(viewModel.AverageSize == 5, "AverageSize was not clamped.", failures);
                        Check(viewModel.TargetFps == 240, "TargetFps was not clamped.", failures);
                        Check(Services.LedLimits.FixedBaudRate == 1000000, "The baud rate is not pinned to 1,000,000.", failures);
                        Check(viewModel.ControllerStatusText.Length > 0, "The controller status line has no text.", failures);
                        Check(!viewModel.IsControllerDetected, "A controller should not be reported before detection.", failures);
                        Check(viewModel.LedPreviewItems.Count == viewModel.TotalLedCount, "Preview was not rebuilt for the new layout.", failures);
                        Check(viewModel.FrameByteSize == Services.LedProtocol.FrameSize(viewModel.TotalLedCount), "Frame byte size does not match the protocol.", failures);
                        Check(viewModel.SettingsPath.Length > 0, "No settings path was resolved.", failures);
                        Check(viewModel.WifiStatusText.Length > 0, "The Wi-Fi status line has no text.", failures);
                        Check(viewModel.NetworkStatusText.Length > 0, "The network status line has no text.", failures);
                        Check(viewModel.SelectedLanDevice is null || viewModel.LanDevices.Contains(viewModel.SelectedLanDevice!), "The selected network device is not in the discovered list.", failures);
                        Check(viewModel.SelectedWifiNetwork is null || viewModel.AvailableWifiNetworks.Contains(viewModel.SelectedWifiNetwork!), "The selected Wi-Fi network is not in the scanned list.", failures);

                        viewModel.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex.ToString());
                }
                finally
                {
                    try
                    {
                        window?.Close();
                    }
                    catch
                    {
                    }

                    Trace.Listeners.Remove(listener);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "The UI thread did not finish in time.");
            Assert.Empty(failures);
            Assert.DoesNotContain(bindingErrors, error => error.Contains("SyncBox", StringComparison.OrdinalIgnoreCase));

            try
            {
                if (System.IO.File.Exists(settingsPath))
                {
                    System.IO.File.Delete(settingsPath);
                }
            }
            catch
            {
            }
        }

        private static void Check(bool condition, string message, List<string> failures)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }

        private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Thread.Sleep(10);
            }
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }

                T? nested = FindDescendant<T>(child);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        private sealed class CapturingListener : TraceListener
        {
            private readonly List<string> _messages;

            public CapturingListener(List<string> messages)
            {
                _messages = messages;
            }

            public override void Write(string? message)
            {
            }

            public override void WriteLine(string? message)
            {
                if (!string.IsNullOrEmpty(message))
                {
                    lock (_messages)
                    {
                        _messages.Add(message);
                    }
                }
            }
        }
    }

    public class WindowCatalogTests
    {
        [Fact]
        public void EnumerationExcludesThisProcessAndReturnsUniqueUsableEntries()
        {
            IReadOnlyList<Services.WindowInfo> windows = Services.WindowCatalog.Enumerate(Environment.ProcessId);

            Assert.DoesNotContain(windows, window => window.ProcessId == Environment.ProcessId);
            Assert.All(windows, window => Assert.NotEqual(IntPtr.Zero, window.Handle));
            Assert.All(windows, window => Assert.False(string.IsNullOrWhiteSpace(window.Title)));
            Assert.Equal(windows.Count, windows.Distinct().Count());
        }

        [Fact]
        public void RegionLookupRejectsWindowsThatAreNotUsable()
        {
            var missing = new Services.WindowInfo(IntPtr.Zero, 1234, "gone", "gone");
            Assert.False(Services.WindowCatalog.TryGetVisibleRegion(missing, out _));
        }
    }
}