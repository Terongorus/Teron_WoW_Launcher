using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TeronWoWLauncher.Native;

namespace TeronWoWLauncher.Services.UI;

/// <summary>
/// Shared fix for any window using its own custom WindowChrome + title row (MainWindow,
/// MarkdownPreviewDialog, AddonDetailsDialog): without this, a maximized chromeless WindowChrome
/// window overhangs the taskbar/screen edge and gets clipped there — the whole UI reads as though
/// it's been pushed in a few pixels from every edge compared to the same window un-maximized.
///
/// The WM_GETMINMAXINFO handling alone (sizing ptMaxSize/ptMaxPosition to the monitor's work area)
/// is NOT sufficient and was the original, incomplete version of this fix: traced via an isolated
/// repro harness, WindowChrome's own internal WM_NCCALCSIZE handling independently re-pads the
/// maximized window's proposed rect back out by its resize-border amount regardless of maximize
/// state, silently undoing the GETMINMAXINFO fix afterward (WM_WINDOWPOSCHANGING correctly lands on
/// the work area; WM_NCCALCSIZE then re-expands rgrc[0] past it; WM_WINDOWPOSCHANGED finalizes on
/// that expanded rect). Clamping rgrc[0] back to the work area in WM_NCCALCSIZE - gated on IsZoomed
/// so normal-state resizing is untouched - is what actually sticks.
/// </summary>
public static class WindowChromeHelper
{
    public static void FixMaximizedBounds(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                => WindowProc(window, hwnd, msg, wParam, lParam, ref handled));
        };
    }

    private static IntPtr WindowProc(Window window, IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCCALCSIZE = 0x0083;

        if (msg == WM_GETMINMAXINFO)
        {
            ApplyMaximizedWorkAreaBounds(window, hwnd, lParam);
            handled = true;
        }
        else if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero && WindowMetrics.IsZoomed(hwnd))
        {
            ClampNcCalcSizeToWorkArea(hwnd, lParam);
        }

        return IntPtr.Zero;
    }

    private static void ApplyMaximizedWorkAreaBounds(Window window, IntPtr hwnd, IntPtr lParam)
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

        // WPF's own default WM_GETMINMAXINFO handling (which normally applies Window.MinWidth/
        // MinHeight to ptMinTrackSize) never gets a chance to run - `handled = true` in WindowProc
        // short-circuits it, since this hook (added via HwndSource.AddHook) runs ahead of WPF's own
        // internal window procedure. Without setting it here too, ptMinTrackSize stays at whatever
        // was already in the (uninitialized) marshaled struct - effectively no minimum at all, which
        // is exactly what let the window shrink far below the declared MinWidth/MinHeight and badly
        // break the 3-column Home layout. DPI-scaled since Win32 message coordinates are physical
        // pixels while MinWidth/MinHeight are 96-DPI-relative WPF units.
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        if (window.MinWidth > 0)
        {
            mmi.ptMinTrackSize.X = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX);
        }

        if (window.MinHeight > 0)
        {
            mmi.ptMinTrackSize.Y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY);
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private static void ClampNcCalcSizeToWorkArea(IntPtr hwnd, IntPtr lParam)
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

        var ncParams = Marshal.PtrToStructure<WindowMetrics.NCCALCSIZE_PARAMS>(lParam);
        ncParams.rgrc0 = monitorInfo.rcWork;
        Marshal.StructureToPtr(ncParams, lParam, true);
    }
}
