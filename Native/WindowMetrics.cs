using System;
using System.Runtime.InteropServices;

namespace TeronWoWLauncher.Native;

/// <summary>
/// P/Invoke declarations used to correctly size a WindowChrome-styled window when maximized (see
/// MainWindow's WM_GETMINMAXINFO handling): without this, Windows sizes a chromeless maximized
/// window to the full monitor bounds rather than the work area, so it overhangs the taskbar/screen
/// edge by the invisible resize-border amount and gets clipped — the "content looks pushed in from
/// the edge" symptom this fixes.
/// </summary>
internal static class WindowMetrics
{
    public const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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
}
