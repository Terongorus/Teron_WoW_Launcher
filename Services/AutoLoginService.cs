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
/// "Loading finished" is detected by polling the game window's responsiveness to a trivial message
/// (WM_NULL via SendMessageTimeout): asset loading keeps the client's main thread — and so its
/// message pump — busy enough to fall behind, an idle login screen answers promptly, so a stretch of
/// consecutive quick responses is a reasonable, version-agnostic signal that the client is ready —
/// instead of guessing a fixed delay that's wrong for any machine other than the one it was tuned on.
/// This used to poll the process's disk I/O counters instead (GetProcessIoCounters via OpenProcess),
/// but that needs a process handle, which can be — and on at least one real machine, was — denied
/// (Win32 error 5) for a process still settling right after CreateSuspended+inject+Resume. A window
/// handle needs no such permission at all, so the window-based check can't hit that wall. The
/// user-configured delay remains a pure fallback, applied only if the window never settles before the
/// overall timeout — not on every successful detection, so it stays a genuine last resort rather than
/// adding wait time to every login.
/// </summary>
public sealed class AutoLoginService
{
    private const int PollIntervalMs = 200;
    private const int RequiredQuietSamples = 4;

    /// <summary>Window-search + loading-settle budget for the native Direct3D9 renderer.</summary>
    public const int DefaultTimeoutMs = 60000;

    /// <summary>
    /// Same budget, doubled — for use when a Direct3D9 hook/translation DLL (e.g. DXVK's d3d9.dll)
    /// is injected. Such a layer has to stand up its own device/instance (a Vulkan instance and
    /// device, in DXVK's case) before the game window appears, which can take noticeably longer
    /// than the native renderer's near-instant device creation, especially on first run (shader/
    /// pipeline cache still cold) or with a slower GPU driver. Without this, <see cref="WaitForGameWindow"/>
    /// can hit its deadline before the window ever shows up, logging "game window not found before
    /// timeout" for a launch that would have succeeded given a bit more patience.
    /// </summary>
    public const int ExtendedTimeoutMs = DefaultTimeoutMs * 2;

    private readonly Logger _log = Logger.Instance;

    public async Task PerformLoginAsync(
        int gameProcessId,
        string account,
        string password,
        int delayMs,
        int timeoutMs = DefaultTimeoutMs,
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
            if (!WaitForLoadingToSettle(hwnd, remainingMs, ct))
            {
                _log.Warn("Auto-login: the game window never settled before the timeout; falling back to the configured delay.");
                Thread.Sleep(Math.Max(0, delayMs));
            }
            ct.ThrowIfCancellationRequested();

            hwnd = FindVisibleWindowForProcess(gameProcessId) is var found && found != IntPtr.Zero ? found : hwnd;

            // SendInput has no concept of a target window — it goes wherever the OS currently has
            // focus. SetForegroundWindow can be silently refused (Windows' foreground-lock heuristic
            // denies a foreground-steal request depending on what last had input focus), so merely
            // requesting focus and proceeding regardless would risk typing the plaintext account/
            // password into whatever window the user was actually looking at. A few short retries
            // absorb normal focus-timing flakiness; if focus still can't be confirmed, abort instead
            // of guessing.
            bool focused = false;
            for (int attempt = 0; attempt < 5 && !focused; attempt++)
            {
                User32.SetForegroundWindow(hwnd);
                Thread.Sleep(150);
                focused = User32.GetForegroundWindow() == hwnd;
            }

            if (!focused)
            {
                _log.Warn("Auto-login: could not confirm the game window has focus; aborting rather than risk typing credentials into the wrong window.");
                return;
            }

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
    /// Polls the game window's responsiveness to a trivial WM_NULL message until it's stayed
    /// responsive for <see cref="RequiredQuietSamples"/> consecutive samples (a proxy for "done
    /// loading assets"), re-asserting foreground focus on every sample so a loading screen can't
    /// steal it away. Needs only the window handle already in hand — no process-level access right
    /// at all, unlike the GetProcessIoCounters/OpenProcess approach this replaced, which could be (and
    /// on at least one real machine, was) denied outright for a process still settling right after
    /// CreateSuspended+inject+Resume. Returns false only if the window never settles before the
    /// deadline — the caller's genuine last-resort fallback, not a routine occurrence now that
    /// measuring at all can't be denied the way opening a process handle could.
    /// </summary>
    private bool WaitForLoadingToSettle(IntPtr hwnd, int timeoutMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        int quietSamples = 0;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(PollIntervalMs);

            User32.SetForegroundWindow(hwnd);

            bool responsive = User32.SendMessageTimeout(
                hwnd, User32.WM_NULL, IntPtr.Zero, IntPtr.Zero,
                User32.SMTO_ABORTIFHUNG | User32.SMTO_BLOCK, PollIntervalMs, out _) != IntPtr.Zero;

            if (responsive)
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
