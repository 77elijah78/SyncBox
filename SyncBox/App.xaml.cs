using System;
using System.Windows;
using System.Windows.Threading;

namespace SyncBox
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, args) => args.Handled = ReportFatal(args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    ReportFatal(ex);
                }
            };
        }

        private static bool ReportFatal(Exception ex)
        {
            MessageBox.Show(
                $"SyncBox hit an unrecoverable error and will close.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "SyncBox",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Current?.Shutdown(1);
            return true;
        }
    }
}