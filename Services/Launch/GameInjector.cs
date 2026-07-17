using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TeronWoWLauncher.Native;

using TeronWoWLauncher.Services.Core;
namespace TeronWoWLauncher.Services.Launch;

/// <summary>Identifiers of the launched game process, for downstream steps (e.g. auto-login).</summary>
public sealed record LaunchResult(int ProcessId);

/// <summary>
/// Launches WoW.exe in a suspended state, loads a list of DLLs into it, then resumes it.
///
/// This is our own implementation of the standard "suspended process + CreateRemoteThread"
/// injection technique. No third-party launcher or injector executable is used or shipped:
/// we create the process, we do the injection, we resume it.
///
/// The launcher process MUST be 32-bit (see the PlatformTarget lock in the csproj). We pass the
/// address of our own kernel32!LoadLibraryW as the remote thread entry point; that address is
/// only valid inside the 32-bit game because kernel32 is mapped at the same base in both
/// same-bitness processes.
/// </summary>
public sealed class GameInjector
{
    private readonly Logger _log = Logger.Instance;

    /// <summary>
    /// Launch <paramref name="wowExePath"/> and inject each path in <paramref name="dllPaths"/>
    /// (already resolved to absolute, existing files, in load order). Throws on any failure,
    /// terminating the suspended process so a half-patched game never runs.
    /// </summary>
    public LaunchResult Launch(string wowExePath, IReadOnlyList<string> dllPaths)
    {
        if (!File.Exists(wowExePath))
        {
            throw new FileNotFoundException($"WoW executable not found: {wowExePath}", wowExePath);
        }

        string fullExe = Path.GetFullPath(wowExePath);
        string? workingDir = Path.GetDirectoryName(fullExe);

        // Resolve LoadLibraryW once, in our own address space.
        IntPtr hKernel32 = Kernel32.GetModuleHandle("kernel32.dll");
        IntPtr pLoadLibraryW = Kernel32.GetProcAddress(hKernel32, "LoadLibraryW");
        if (pLoadLibraryW == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not resolve kernel32!LoadLibraryW.");
        }

        var startupInfo = new Kernel32.STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf<Kernel32.STARTUPINFO>();

        // Give CreateProcess a writable command-line buffer (it may modify it in place).
        var commandLine = new StringBuilder("\"" + fullExe + "\"", 1024);

        _log.Info($"Launching (suspended): {fullExe}");
        uint flags = Kernel32.CREATE_SUSPENDED | Kernel32.CREATE_UNICODE_ENVIRONMENT;

        bool created = Kernel32.CreateProcess(
            fullExe,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            bInheritHandles: false,
            flags,
            IntPtr.Zero,
            workingDir,
            ref startupInfo,
            out Kernel32.PROCESS_INFORMATION pi);

        if (!created)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err,
                $"CreateProcess failed for {fullExe} (Win32 error {err}). " +
                "This can happen if compatibility mode is enabled on the WoW executable.");
        }

        _log.Info($"Game process created (PID {pi.dwProcessId}); injecting {dllPaths.Count} DLL(s)...");

        try
        {
            foreach (string dll in dllPaths)
            {
                RemoteLoadLibrary(pi.hProcess, pLoadLibraryW, dll);
            }
        }
        catch
        {
            _log.Error("Injection failed; terminating the suspended game process.");
            try { Kernel32.TerminateProcess(pi.hProcess, 1); } catch { /* best effort */ }
            CloseHandles(pi);
            throw;
        }

        _log.Info("All DLLs injected; resuming game.");
        Kernel32.ResumeThread(pi.hThread);

        int pid = pi.dwProcessId;
        CloseHandles(pi);
        return new LaunchResult(pid);
    }

    private void RemoteLoadLibrary(IntPtr hProcess, IntPtr pLoadLibraryW, string dllPath)
    {
        _log.Info($"  Injecting: {dllPath}");

        // Written as a wide (UTF-16) NUL-terminated string, to match LoadLibraryW.
        byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        uint size = (uint)pathBytes.Length;

        IntPtr remotePath = Kernel32.VirtualAllocEx(
            hProcess, IntPtr.Zero, size, Kernel32.MEM_RESERVE | Kernel32.MEM_COMMIT, Kernel32.PAGE_READWRITE);
        if (remotePath == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"VirtualAllocEx failed for {dllPath}");
        }

        try
        {
            if (!Kernel32.WriteProcessMemory(hProcess, remotePath, pathBytes, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory failed for {dllPath}");
            }

            IntPtr hThread = Kernel32.CreateRemoteThread(
                hProcess, IntPtr.Zero, 0, pLoadLibraryW, remotePath, 0, IntPtr.Zero);
            if (hThread == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateRemoteThread failed for {dllPath}");
            }

            try
            {
                uint wait = Kernel32.WaitForSingleObject(hThread, 10000);
                if (wait != Kernel32.WAIT_OBJECT_0)
                {
                    throw new TimeoutException($"Timed out waiting for LoadLibraryW to load {dllPath}.");
                }

                // On x86, the thread exit code holds the low 32 bits of LoadLibraryW's HMODULE:
                // nonzero on success, 0 (NULL) on failure.
                Kernel32.GetExitCodeThread(hThread, out uint exitCode);
                if (exitCode == 0)
                {
                    throw new InvalidOperationException(
                        $"LoadLibraryW returned NULL for {dllPath}. The DLL failed to load — likely a " +
                        "missing dependency, wrong architecture (must be 32-bit), or an incompatible client.");
                }
            }
            finally
            {
                Kernel32.CloseHandle(hThread);
            }
        }
        finally
        {
            Kernel32.VirtualFreeEx(hProcess, remotePath, 0, Kernel32.MEM_RELEASE);
        }
    }

    private static void CloseHandles(Kernel32.PROCESS_INFORMATION pi)
    {
        if (pi.hThread != IntPtr.Zero) { Kernel32.CloseHandle(pi.hThread); }
        if (pi.hProcess != IntPtr.Zero) { Kernel32.CloseHandle(pi.hProcess); }
    }
}
