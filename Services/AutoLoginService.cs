using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TeronWoWLauncher.Native;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Types the account name and password into the WoW login screen after launch, then presses
/// Enter. Our own implementation of the AutoLogin approach: find the game's visible window, focus
/// it, and synthesize keystrokes with SendInput. Improved over the reference by using VkKeyScan's
/// full shift state so passwords containing shifted symbols (!, @, etc.) type correctly, not just
/// upper-case letters.
///
/// Note: like any SendInput-based login, the game window must be able to take the foreground, and
/// the account-name field must have focus (it does by default on the vanilla login screen). The
/// configurable delay covers the gap between the window first appearing and the login screen
/// actually becoming interactive.
/// </summary>
public sealed class AutoLoginService
{
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
            _log.Info("Auto-login: waiting for the game window...");
            IntPtr hwnd = WaitForGameWindow(gameProcessId, timeoutMs, ct);
            if (hwnd == IntPtr.Zero)
            {
                _log.Warn("Auto-login: game window not found before timeout; skipping.");
                return;
            }

            Thread.Sleep(Math.Max(0, delayMs));
            ct.ThrowIfCancellationRequested();

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

    private void SendString(string text)
    {
        foreach (char ch in text)
        {
            SendChar(ch);
            Thread.Sleep(10);
        }
    }

    private void SendChar(char ch)
    {
        short scan = User32.VkKeyScan(ch);
        if (scan == -1)
        {
            _log.Warn($"Auto-login: character '{ch}' cannot be typed on the current keyboard layout; skipping it.");
            return;
        }

        ushort vk = (ushort)(scan & 0xFF);
        bool shift = ((scan >> 8) & 0x01) != 0;

        if (shift)
        {
            SendKey(User32.VK_SHIFT, keyUp: false);
        }

        SendKey(vk, keyUp: false);
        SendKey(vk, keyUp: true);

        if (shift)
        {
            SendKey(User32.VK_SHIFT, keyUp: true);
        }
    }

    private static void PressVk(ushort vk)
    {
        SendKey(vk, keyUp: false);
        SendKey(vk, keyUp: true);
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
