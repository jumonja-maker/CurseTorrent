using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CurseTorrent.App;

/// <summary>Keeps each top-level window within its current monitor's work area.</summary>
internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    internal static void Attach(Window window)
    {
        double declaredMinWidth = window.MinWidth;
        double declaredMinHeight = window.MinHeight;
        bool closed = false;
        void Constrain()
        {
            if (closed || !window.IsLoaded || window.WindowState != WindowState.Normal) return;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect) ||
                !GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
            int availableWidth = monitor.Work.Right - monitor.Work.Left;
            int availableHeight = monitor.Work.Bottom - monitor.Work.Top;
            if (availableWidth <= 0 || availableHeight <= 0) return;
            double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
            window.MinWidth = Math.Min(declaredMinWidth, availableWidth / scale);
            window.MinHeight = Math.Min(declaredMinHeight, availableHeight / scale);
            // Restoring a larger minimum may resize the native window immediately.
            if (!GetWindowRect(handle, out rect)) return;
            int width = Math.Min(rect.Right - rect.Left, availableWidth);
            int height = Math.Min(rect.Bottom - rect.Top, availableHeight);
            int left = Math.Clamp(rect.Left, monitor.Work.Left, monitor.Work.Right - width);
            int top = Math.Clamp(rect.Top, monitor.Work.Top, monitor.Work.Bottom - height);
            if (width == rect.Right - rect.Left && height == rect.Bottom - rect.Top && left == rect.Left && top == rect.Top) return;
            // Native coordinates remain correct across monitors with different DPI.
            // Also update the requested WPF size so subsequent layout cannot restore
            // the original oversized dimensions.
            window.Width = width / scale;
            window.Height = height / scale;
            _ = SetWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010 | 0x0200);
        }
        void Schedule() => window.Dispatcher.BeginInvoke(new Action(Constrain), DispatcherPriority.Loaded);
        PropertyChangedEventHandler parametersChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.WorkArea)) Schedule();
        };
        window.Loaded += (_, _) => Constrain();
        window.DpiChanged += (_, _) => Schedule();
        window.StateChanged += (_, _) => Schedule();
        SystemParameters.StaticPropertyChanged += parametersChanged;
        window.Closed += (_, _) =>
        {
            closed = true;
            SystemParameters.StaticPropertyChanged -= parametersChanged;
        };
    }
}
