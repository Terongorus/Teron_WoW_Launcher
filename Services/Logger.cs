using System;
using System.IO;
using System.Text;

namespace TeronWoWLauncher.Services;

public enum LogLevel { Debug, Info, Warn, Error }

public readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Message);

/// <summary>
/// Minimal, dependency-free logger. Writes to a per-day file under
/// %LocalAppData%\TeronWoWLauncher\Logs, and raises <see cref="MessageLogged"/> so the UI can
/// show output live. Logging must never throw, so all file I/O is guarded.
/// </summary>
public sealed class Logger
{
    private static readonly Lazy<Logger> _instance = new(() => new Logger());
    public static Logger Instance => _instance.Value;

    private readonly object _gate = new();

    public string LogFilePath { get; }

    public event EventHandler<LogEntry>? MessageLogged;

    private Logger()
    {
        try
        {
            AppPaths.EnsureDataRoot();
            Directory.CreateDirectory(AppPaths.LogsDirectory);
        }
        catch { /* fall back to the data root below */ }

        var dir = Directory.Exists(AppPaths.LogsDirectory) ? AppPaths.LogsDirectory : AppPaths.DataRoot;
        LogFilePath = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd}.log");

        Info($"=== Teron WoW Launcher started (PID {Environment.ProcessId}, " +
             $"{(Environment.Is64BitProcess ? "x64" : "x86")}) ===");
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex}");

    private void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";

        lock (_gate)
        {
            try { File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging must never throw */ }
        }

        MessageLogged?.Invoke(this, entry);
    }
}
