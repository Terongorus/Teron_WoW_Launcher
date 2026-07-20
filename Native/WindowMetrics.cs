using System;
using System.Runtime.InteropServices;

namespace TeronWoWLauncher.Native;

/// <summary>
/// P/Invoke declarations used to correctly size a WindowChrome-styled window when maximized (see
/// WindowChromeHelper's WM_NCCALCSIZE handling): System.Windows.Shell.WindowChrome pads a maximized
/// chromeless window's rect back out by its own resize-border amount regardless of maximize state,
/// so it overhangs the taskbar/screen edge and gets clipped — the "content looks pushed in from the
/// edge" symptom this fixes.
/// </summary>
internal static class WindowMetrics
{
    public const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Only rgrc[0] (the proposed new window rect, in/out) is used here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0;
        public RECT rgrc1;
        public RECT rgrc2;
        public IntPtr lppos;
    }
}
