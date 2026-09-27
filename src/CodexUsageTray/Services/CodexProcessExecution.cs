using System.Diagnostics;
using System.Text;

namespace CodexUsageTray;

internal interface ICodexProcessExecution
{
    Task<TResult> ExchangeLinesAsync<TResult>(
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken);

    Task<TResult> ExchangeReadOnlyLinesAsync<TResult>(
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken) =>
        ExchangeLinesAsync(timeout, exchange, cancellationToken);
}

internal interface ICodexLineExchange
{
    string? LastStandardErrorLine { get; }
    bool IsInitialized => false;
    void MarkInitialized() { }
    void DiscardConnection() { }
    Task WriteLineAsync(string line);
    ValueTask<string?> ReadLineAsync();
}

internal sealed class WindowsCodexProcessExecution : ICodexProcessExecution, IAsyncDisposable
{
    private static readonly Encoding Utf8WithoutByteOrderMark = new UTF8Encoding(false);
    private readonly SemaphoreSlim appServerGate = new(1, 1);
    private Process? appServer;
    private bool appServerInitialized;
    private bool discardAppServerAfterExchange;
    private (bool Exists, long Length, DateTime LastWriteUtc) authFileState;
    private DateTime appServerStartedUtc;
    private bool disposed;
    private int reconnectRequested;

    public void RequestReconnect() => Interlocked.Exchange(ref reconnectRequested, 1);

    public async Task<TResult> ExchangeReadOnlyLinesAsync<TResult>(
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ExchangeLinesAsync(timeout, exchange, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt == 0
                && !cancellationToken.IsCancellationRequested
                && IsTransportFailure(exception))
            {
                // The failed exchange discarded its child; replay only account reads.
            }
        }
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException or CodexAppServerDisconnectedException or OperationCanceledException
        || exception.InnerException is IOException or CodexAppServerDisconnectedException
            or OperationCanceledException or TimeoutException;

    public async Task<TResult> ExchangeLinesAsync<TResult>(
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken)
    {
        await appServerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var currentAuthFileState = ReadAuthFileState();
            var reconnect = Interlocked.Exchange(ref reconnectRequested, 0) != 0;
            if (reconnect || appServer is null || appServer.HasExited
                || authFileState != currentAuthFileState
                || DateTime.UtcNow - appServerStartedUtc >= TimeSpan.FromMinutes(30))
            {
                StopAppServer();
                deadline.Token.ThrowIfCancellationRequested();
                appServer = Start(CodexCommandLocator.Find());
                appServerStartedUtc = DateTime.UtcNow;
                authFileState = currentAuthFileState;
                appServerInitialized = false;
                appServer.ErrorDataReceived += RecordAppServerError;
                appServer.BeginErrorReadLine();
            }

            var lines = new PersistentAppServerLines(this, appServer, deadline.Token);
            try
            {
                var result = await exchange(lines).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (discardAppServerAfterExchange)
                {
                    StopAppServer();
                }
                return result;
            }
            catch (InvalidOperationException exception)
                when (exception is CodexAuthenticationExpiredException or CodexAuthenticationRequiredException)
            {
                // Other pipelined responses may still be queued with IDs the recovery request reuses.
                StopAppServer();
                throw;
            }
            catch
            {
                StopAppServer();
                throw;
            }
        }
        catch
        {
            if (appServer is not null && !appServerInitialized)
            {
                StopAppServer();
            }
            throw;
        }
        finally
        {
            appServerGate.Release();
        }
    }

    private string? lastAppServerError;

    private void RecordAppServerError(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            Volatile.Write(ref lastAppServerError, eventArgs.Data);
        }
    }

    private static (bool Exists, long Length, DateTime LastWriteUtc) ReadAuthFileState()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        var file = new FileInfo(Path.Combine(home, "auth.json"));
        return file.Exists ? (true, file.Length, file.LastWriteTimeUtc) : (false, 0, default);
    }

    private void StopAppServer()
    {
        var process = appServer;
        appServer = null;
        appServerInitialized = false;
        discardAppServerAfterExchange = false;
        Volatile.Write(ref lastAppServerError, null);
        if (process is null)
        {
            return;
        }

        TryStopProcess(process);
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await appServerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed)
            {
                disposed = true;
                StopAppServer();
            }
        }
        finally
        {
            appServerGate.Release();
        }
    }

    private sealed class PersistentAppServerLines(
        WindowsCodexProcessExecution owner,
        Process process,
        CancellationToken cancellationToken) : ICodexLineExchange
    {
        public string? LastStandardErrorLine => Volatile.Read(ref owner.lastAppServerError);
        public bool IsInitialized => owner.appServerInitialized;
        public void MarkInitialized() => owner.appServerInitialized = true;
        public void DiscardConnection() => owner.discardAppServerAfterExchange = true;

        public async Task WriteLineAsync(string line)
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<string?> ReadLineAsync() => process.StandardOutput.ReadLineAsync(cancellationToken);
    }

    private static Process Start(string codexPath)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /s /c \"\"{codexPath}\" app-server --stdio\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8WithoutByteOrderMark,
            StandardErrorEncoding = Utf8WithoutByteOrderMark,
            StandardInputEncoding = Utf8WithoutByteOrderMark
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the Codex CLI.");
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }
}
