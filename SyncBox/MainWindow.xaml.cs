using System;
using System.ComponentModel;
using System.Windows;
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
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _viewModel?.Dispose();
            _viewModel = null;
            base.OnClosing(e);
        }
    }
}