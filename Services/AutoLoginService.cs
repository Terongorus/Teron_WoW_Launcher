using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Native;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Types the account name and password into the WoW login screen after launch, then presses
/// Enter. Our own implementation of the AutoLogin approach: find the game's visible window, keep
/// it focused while it loads, wait for loading to actually finish, and synthesize the credentials.
///
/// Characters are typed with SendInput's KEYEVENTF_UNICODE flag, which delivers the literal
/// character directly rather than a virtual-key press translated through a keyboard layout. A
/// VK-based approach (looking up which key produces a character on the *current* layout) breaks
/// as soon as the system's active layout isn't English: letters either come out wrong or can't be
/// found at all and get silently dropped, corrupting the password and tripping the client's
/// failed-login lockout. Unicode injection sidesteps layout translation entirely, so this works
/// the same regardless of what keyboard layout is active. Tab/Enter are still sent as real
/// virtual-key presses since those are control keys, not characters — layout never affects them.
///
/// "Loading finished" is detected by polling the process's disk I/O counters: asset loading is
/// I/O-heavy, an idle login screen is not, so a stretch of near-zero read/write activity is a
/// reasonable, version-agnostic signal that the client is ready — instead of guessing a fixed
/// delay that's wrong for any disk speed other than the one it was tuned on. The configured delay
/// is kept only as a fallback for the rare case the I/O counters can't be read.
/// </summary>
public sealed class AutoLoginService
{
    private const int PollIntervalMs = 200;
    private const int RequiredQuietSamples = 4;
    private const ulong QuietThresholdBytes = 64 * 1024;

    private readonly Logger _log = Logger.Instance;

    public async Task PerformLoginAsync(
        int gameProcessId,
        string account,
        string password,
        int delayMs,
        int timeoutMs = 60000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account))
        {
            _log.Warn("Auto-login: no account configured; skipping.");
            return;
        }

        await Task.Run(() =>
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            _log.Info("Auto-login: waiting for the game window...");
            IntPtr hwnd = WaitForGameWindow(gameProcessId, timeoutMs, ct);
            if (hwnd == IntPtr.Zero)
            {
                _log.Warn("Auto-login: game window not found before timeout; skipping.");
                return;
            }

            User32.SetForegroundWindow(hwnd);

            _log.Info("Auto-login: waiting for the client to finish loading...");
            int remainingMs = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (!WaitForLoadingToSettle(gameProcessId, hwnd, remainingMs, ct))
            {
                _log.Warn("Auto-login: could not measure load activity; falling back to the configured delay.");
                Thread.Sleep(Math.Max(0, delayMs));
            }
            ct.ThrowIfCancellationRequested();

            hwnd = FindVisibleWindowForProcess(gameProcessId) is var found && found != IntPtr.Zero ? found : hwnd;
            if (!User32.SetForegroundWindow(hwnd))
            {
                _log.Warn("Auto-login: could not bring the game to the foreground; keystrokes may miss.");
            }

            Thread.Sleep(150);

            _log.Info("Auto-login: sending credentials.");
            SendString(account);
            Thread.Sleep(40);
            PressVk(User32.VK_TAB);
            Thread.Sleep(40);
            SendString(password);
            Thread.Sleep(40);
            PressVk(User32.VK_RETURN);
            _log.Info("Auto-login: credentials sent.");
        }, ct);
    }

    /// <summary>
    /// Polls the process's disk I/O counters and the game window's foreground state until read/write
    /// activity has stayed quiet for <see cref="RequiredQuietSamples"/> consecutive samples (a proxy
    /// for "done loading assets"), re-asserting foreground focus on every sample so a loading screen
    /// can't steal it away. Returns false if the counters can't be read at all, so the caller can fall
    /// back to a fixed delay instead of proceeding with no wait whatsoever.
    /// </summary>
    private bool WaitForLoadingToSettle(int pid, IntPtr hwnd, int timeoutMs, CancellationToken ct)
    {
        // Opened with the minimal right GetProcessIoCounters actually needs, rather than going
        // through System.Diagnostics.Process.Handle (which requests full PROCESS_ALL_ACCESS and can
        // be denied even for a process we just created ourselves).
        IntPtr hProcess = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess == IntPtr.Zero)
        {
            _log.Warn($"Auto-login: could not open the game process for load monitoring (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            if (!TryGetIoCounters(hProcess, out ulong lastRead, out ulong lastWrite))
            {
                _log.Warn($"Auto-login: could not read the game process's I/O counters (Win32 error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            long deadline = Environment.TickCount64 + timeoutMs;
            int quietSamples = 0;

            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(PollIntervalMs);

                User32.SetForegroundWindow(hwnd);

                if (!TryGetIoCounters(hProcess, out ulong read, out ulong write))
                {
                    _log.Warn($"Auto-login: could not read the game process's I/O counters (Win32 error {Marshal.GetLastWin32Error()}).");
                    return false;
                }

                ulong deltaRead = read - lastRead;
                ulong deltaWrite = write - lastWrite;
                lastRead = read;
                lastWrite = write;

                if (deltaRead < QuietThresholdBytes && deltaWrite < QuietThresholdBytes)
                {
                    quietSamples++;
                    if (quietSamples >= RequiredQuietSamples)
                    {
                        return true;
                    }
                }
                else
                {
                    quietSamples = 0;
                }
            }
        }
        finally
        {
            Kernel32.CloseHandle(hProcess);
        }

        _log.Warn("Auto-login: load activity never went quiet before the timeout; proceeding anyway.");
        return true;
    }

    private static bool TryGetIoCounters(IntPtr hProcess, out ulong read, out ulong write)
    {
        if (Kernel32.GetProcessIoCounters(hProcess, out Kernel32.IO_COUNTERS counters))
        {
            read = counters.ReadTransferCount;
            write = counters.WriteTransferCount;
            return true;
        }

        read = 0;
        write = 0;
        return false;
    }

    private IntPtr WaitForGameWindow(int pid, int timeoutMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            IntPtr found = FindVisibleWindowForProcess(pid);
            if (found != IntPtr.Zero)
            {
                return found;
            }

            Thread.Sleep(200);
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindVisibleWindowForProcess(int pid)
    {
        IntPtr result = IntPtr.Zero;
        HashSet<int> childPids = GetChildProcessIds(pid);

        // Keep the delegate in a local so it can't be collected during the synchronous enumeration.
        User32.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!User32.IsWindowVisible(hwnd))
            {
                return true;
            }

            User32.GetWindowThreadProcessId(hwnd, out uint winPid);
            if (winPid == (uint)pid || childPids.Contains((int)winPid))
            {
                result = hwnd;
                return false; // stop enumerating
            }

            return true;
        };

        User32.EnumWindows(callback, IntPtr.Zero);
        return result;
    }

    private static HashSet<int> GetChildProcessIds(int parentPid)
    {
        var children = new HashSet<int>();
        IntPtr snapshot = Kernel32.CreateToolhelp32Snapshot(Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return children;
        }

        try
        {
            var pe = new Kernel32.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<Kernel32.PROCESSENTRY32>() };
            if (Kernel32.Process32First(snapshot, ref pe))
            {
                do
                {
                    if (pe.th32ParentProcessID == (uint)parentPid)
                    {
                        children.Add((int)pe.th32ProcessID);
                    }
                }
                while (Kernel32.Process32Next(snapshot, ref pe));
            }
        }
        finally
        {
            Kernel32.CloseHandle(snapshot);
        }

        return children;
    }

    private static void SendString(string text)
    {
        foreach (char ch in text)
        {
            SendChar(ch);
            Thread.Sleep(10);
        }
    }

    private static void SendChar(char ch)
    {
        SendUnicodeKey(ch, keyUp: false);
        SendUnicodeKey(ch, keyUp: true);
    }

    private static void PressVk(ushort vk)
    {
        SendKey(vk, keyUp: false);
        SendKey(vk, keyUp: true);
    }

    private static void SendUnicodeKey(char ch, bool keyUp)
    {
        var input = new User32.INPUT
        {
            type = User32.INPUT_KEYBOARD,
            U = new User32.InputUnion
            {
                ki = new User32.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = User32.KEYEVENTF_UNICODE | (keyUp ? User32.KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        User32.SendInput(1, new[] { input }, Marshal.SizeOf<User32.INPUT>());
    }

    private static void SendKey(ushort vk, bool keyUp)
    {
        var input = new User32.INPUT
        {
            type = User32.INPUT_KEYBOARD,
            U = new User32.InputUnion
            {
                ki = new User32.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? User32.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        User32.SendInput(1, new[] { input }, Marshal.SizeOf<User32.INPUT>());
    }
}
