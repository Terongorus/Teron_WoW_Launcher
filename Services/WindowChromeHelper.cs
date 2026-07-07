using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TeronWoWLauncher.Native;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Shared fix for any window using its own custom WindowChrome + title row (MainWindow,
/// MarkdownPreviewDialog): without this, Windows sizes a maximized chromeless window to the full
/// monitor bounds instead of the work area, so it overhangs the taskbar/screen edge by the invisible
/// resize-border thickness and gets clipped there — the whole UI reads as though it's been pushed in
/// a few pixels from every edge compared to the same window un-maximized.
/// </summary>
public static class WindowChromeHelper
{
    public static void FixMaximizedBounds(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        };
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyMaximizedWorkAreaBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyMaximizedWorkAreaBounds(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = WindowMetrics.MonitorFromWindow(hwnd, WindowMetrics.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new WindowMetrics.MONITORINFO { cbSize = Marshal.SizeOf<WindowMetrics.MONITORINFO>() };
        if (!WindowMetrics.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        WindowMetrics.RECT workArea = monitorInfo.rcWork;
        WindowMetrics.RECT monitorArea = monitorInfo.rcMonitor;

        var mmi = Marshal.PtrToStructure<WindowMetrics.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        mmi.ptMaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        mmi.ptMaxSize.X = workArea.Right - workArea.Left;
        mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
        Marshal.StructureToPtr(mmi, lParam, true);
    }
}
