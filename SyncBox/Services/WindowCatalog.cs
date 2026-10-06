using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SyncBox.Services
{
    public sealed class WindowInfo : IEquatable<WindowInfo>
    {
        public WindowInfo(IntPtr handle, int processId, string title, string processName)
        {
            Handle = handle;
            ProcessId = processId;
            Title = title;
            ProcessName = processName;
        }

        public IntPtr Handle { get; }

        public int ProcessId { get; }

        public string Title { get; }

        public string ProcessName { get; }

        public string Display => $"{Title}  —  {ProcessName} ({ProcessId})";

        public bool Equals(WindowInfo? other) =>
            other is not null && other.Handle == Handle && other.ProcessId == ProcessId;

        public override bool Equals(object? obj) => Equals(obj as WindowInfo);

        public override int GetHashCode() => HashCode.Combine(Handle, ProcessId);

        public override string ToString() => Display;
    }

    public static class WindowCatalog
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public static IReadOnlyList<WindowInfo> Enumerate(int ownProcessId)
        {
            var result = new List<WindowInfo>();
            var seen = new HashSet<long>();
            Process[] processes;

            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return result;
            }

            foreach (Process process in processes)
            {
                try
                {
                    if (process.Id == ownProcessId || process.HasExited)
                    {
                        continue;
                    }

                    IntPtr handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !IsWindow(handle))
                    {
                        continue;
                    }

                    string title = process.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    string name;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch
                    {
                        name = "?";
                    }

                    if (!seen.Add(handle.ToInt64()))
                    {
                        continue;
                    }

                    result.Add(new WindowInfo(handle, process.Id, title.Trim(), name));
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            result.Sort(static (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        public static bool TryGetVisibleRegion(WindowInfo window, out CaptureRegion region)
        {
            region = default;
            if (window is null || window.Handle == IntPtr.Zero)
            {
                return false;
            }

            if (!IsWindow(window.Handle) || IsIconic(window.Handle) || !IsWindowVisible(window.Handle))
            {
                return false;
            }

            if (!GetWindowRect(window.Handle, out RECT rect))
            {
                return false;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            region = new CaptureRegion(rect.Left, rect.Top, width, height);
            return true;
        }
    }
}