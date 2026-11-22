using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CurseTorrent.App;

internal static class WindowTheme
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    internal static void Attach(Window window)
    {
        WindowPlacement.Attach(window);
        window.SourceInitialized += (_, _) => ApplyNativeTheme(window);
    }

    internal static void ApplyNativeTheme(Window window)
    {
        if (!window.IsInitialized) return;
        int enabled = SystemParameters.HighContrast ? 0 : 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }
}
