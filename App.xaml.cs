using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TeronWoWLauncher.Services.Addons;
using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Dlls;
using TeronWoWLauncher.Services.Launch;
using TeronWoWLauncher.Services.Patching;
using TeronWoWLauncher.Services.UI;

namespace TeronWoWLauncher;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "TeronWoWLauncher.SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: a second launcher could fight over dlls.txt or the game process.
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                $"{AppInfo.DisplayName} is already running.",
                AppInfo.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Crash logging: append any unhandled exception to error.log. Logging must never throw.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        // Leave e.Handled = false so the app still fails loudly after the crash is recorded.
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not the owner / already released */ }
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
