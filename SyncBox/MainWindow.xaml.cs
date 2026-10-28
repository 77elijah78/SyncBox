using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SyncBox.ViewModels;

namespace SyncBox
{
    public partial class MainWindow : Window
    {
        private MainViewModel? _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            WifiPasswordBox.Password = _viewModel.WifiPassword;
            FitToWorkArea();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _viewModel?.Dispose();
            _viewModel = null;
            base.OnClosing(e);
        }

        private void OnWifiPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel is not null && sender is PasswordBox box)
            {
                _viewModel.WifiPassword = box.Password;
            }
        }

        /// <summary>
        /// The window opens tall on a 1080p desk but must still fit a small laptop
        /// screen, where the tab content simply scrolls instead.
        /// </summary>
        private void FitToWorkArea()
        {
            Rect workArea = SystemParameters.WorkArea;
            if (workArea.Height > 0 && Height > workArea.Height - 24)
            {
                Height = Math.Max(MinHeight, workArea.Height - 24);
            }

            if (workArea.Width > 0 && Width > workArea.Width - 24)
            {
                Width = Math.Max(MinWidth, workArea.Width - 24);
            }
        }
    }
}
