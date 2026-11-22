using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CurseTorrent.App;

internal static class WindowTheme
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    internal static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        int enabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    };
}
