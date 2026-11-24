using System.IO;
using System.IO.Pipes;
using System.Text;
using TeronWoWLauncher.Services.Core;

namespace TeronWoWLauncher.Services.Launch;

/// <summary>
/// Hosts a per-process, current-user-only named pipe (<c>TeronAutoLogin_&lt;pid&gt;</c>) that
/// delivers DPAPI-decrypted credentials in memory to the injected <c>TeronAutoLogin.dll</c>
/// helper and reads back its initialization status (<c>HOOK_OK</c> or <c>HOOK_FAILED</c>).
/// </summary>
public sealed class AutoLoginPipeServer : IDisposable
{
    public const string HelperDllFileName = "TeronAutoLogin.dll";

    private readonly Logger _log = Logger.Instance;
    private readonly NamedPipeServerStream _pipe;

    private AutoLoginPipeServer(int processId)
    {
        _pipe = new NamedPipeServerStream(
            $"TeronAutoLogin_{processId}",
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// Resolves the bundled <c>TeronAutoLogin.dll</c> next to <c>TeronWoWLauncher.exe</c>, or null
    /// if missing (in which case callers fall back to <see cref="AutoLoginService"/>).
    /// </summary>
    public static string? ResolveHelperDllPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, HelperDllFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Starts listening for the helper DLL before the game process is resumed so the pipe is
    /// guaranteed to exist as soon as <c>DllMain</c> spawns its worker thread.
    /// </summary>
    public static AutoLoginPipeServer StartListening(int processId) => new(processId);

    /// <summary>
    /// Waits for the helper DLL to connect, sends the credentials in memory, and returns true if
    /// the DLL reported <c>HOOK_OK</c>. Returns false on timeout, I/O error, or <c>HOOK_FAILED</c>.
    /// </summary>
    public async Task<bool> HandshakeAsync(
        string account,
        string password,
        bool rememberAccountName,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            await _pipe.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);

            byte[] acctBytes = Encoding.UTF8.GetBytes(account);
            byte[] passBytes = Encoding.UTF8.GetBytes(password);
            byte[] rememberFlag = [rememberAccountName ? (byte)1 : (byte)0];

            await WriteInt32Async(acctBytes.Length, timeoutCts.Token).ConfigureAwait(false);
            await _pipe.WriteAsync(acctBytes, timeoutCts.Token).ConfigureAwait(false);
            await WriteInt32Async(passBytes.Length, timeoutCts.Token).ConfigureAwait(false);
            if (passBytes.Length > 0)
            {
                await _pipe.WriteAsync(passBytes, timeoutCts.Token).ConfigureAwait(false);
            }
            await _pipe.WriteAsync(rememberFlag, timeoutCts.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string? response = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.Equals(response, "HOOK_OK", StringComparison.Ordinal))
            {
                return true;
            }

            _log.Warn($"Auto-login helper reported '{response ?? "<eof>"}'; falling back to SendInput.");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn("Auto-login helper pipe handshake timed out; falling back to SendInput.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log.Warn($"Auto-login helper pipe error ({ex.Message}); falling back to SendInput.");
            return false;
        }
    }

    private async Task WriteInt32Async(int value, CancellationToken ct)
    {
        byte[] buf = BitConverter.GetBytes(value);
        await _pipe.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    public void Dispose() => _pipe.Dispose();
}

