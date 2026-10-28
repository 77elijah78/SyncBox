using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SyncBox
{
    /// <summary>
    /// Asks DWM to paint the non-client area (title bar and border) dark so the
    /// Windows frame matches the application theme.
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        public static void Apply(Window window)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                int enabled = 1;
                int result = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkMode,
                    ref enabled,
                    Marshal.SizeOf<int>());
                if (result != 0)
                {
                    DwmSetWindowAttribute(
                        handle,
                        DwmwaUseImmersiveDarkModeBefore20H1,
                        ref enabled,
                        Marshal.SizeOf<int>());
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref int attributeValue,
            int attributeSize);
    }
}
