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
    internal const int MaxProtocolLineChars = 1_048_576;
    private const int MaxDiagnosticLineChars = 4_096;
    private static readonly Encoding Utf8WithoutByteOrderMark = new UTF8Encoding(false);
    private readonly SemaphoreSlim appServerGate = new(1, 1);
    private Process? appServer;
    private bool appServerInitialized;
    private bool discardAppServerAfterExchange;
    private AppServerDiagnostics? appServerDiagnostics;
    private CancellationTokenSource? appServerErrorCancellation;
    private Task? appServerErrorDrain;
    private bool skipLineFeed;
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
                appServerDiagnostics = new AppServerDiagnostics();
                appServerErrorCancellation = new CancellationTokenSource();
                appServerErrorDrain = DrainStandardErrorAsync(
                    appServer.StandardError,
                    appServerDiagnostics,
                    appServerErrorCancellation.Token);
            }

            var lines = new PersistentAppServerLines(this, appServer, appServerDiagnostics!, deadline.Token);
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

    private static async Task DrainStandardErrorAsync(
        StreamReader reader,
        AppServerDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new char[MaxDiagnosticLineChars];
        var next = 0;
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            foreach (var character in buffer.AsSpan(0, read))
            {
                if (character is '\r' or '\n')
                {
                    RecordLine();
                    next = 0;
                    count = 0;
                    continue;
                }

                line[next] = character;
                next = (next + 1) % line.Length;
                count = Math.Min(count + 1, line.Length);
            }
        }

        RecordLine();

        void RecordLine()
        {
            if (count == 0)
            {
                return;
            }

            var start = count == line.Length ? next : 0;
            var value = string.Create(count, (line, start), static (result, state) =>
            {
                for (var index = 0; index < result.Length; index++)
                {
                    result[index] = state.line[(state.start + index) % state.line.Length];
                }
            });
            if (!string.IsNullOrWhiteSpace(value))
            {
                diagnostics.Record(value);
            }
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
        var errorCancellation = appServerErrorCancellation;
        var errorDrain = appServerErrorDrain;
        appServer = null;
        appServerInitialized = false;
        discardAppServerAfterExchange = false;
        appServerDiagnostics = null;
        appServerErrorCancellation = null;
        appServerErrorDrain = null;
        skipLineFeed = false;
        if (process is null)
        {
            return;
        }

        errorCancellation?.Cancel();
        TryStopProcess(process);
        try
        {
            errorDrain?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Cancellation or process shutdown can interrupt the diagnostic reader.
        }
        errorCancellation?.Dispose();
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
        AppServerDiagnostics diagnostics,
        CancellationToken cancellationToken) : ICodexLineExchange
    {
        private readonly char[] lineCharacter = new char[1];

        public string? LastStandardErrorLine => diagnostics.LastLine;
        public bool IsInitialized => owner.appServerInitialized;
        public void MarkInitialized() => owner.appServerInitialized = true;
        public void DiscardConnection() => owner.discardAppServerAfterExchange = true;

        public async Task WriteLineAsync(string line)
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<string?> ReadLineAsync()
        {
            var line = new StringBuilder();
            while (await process.StandardOutput.ReadAsync(lineCharacter.AsMemory(), cancellationToken)
                       .ConfigureAwait(false) != 0)
            {
                var character = lineCharacter[0];
                if (owner.skipLineFeed)
                {
                    owner.skipLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                if (character is '\r' or '\n')
                {
                    owner.skipLineFeed = character == '\r';
                    return line.ToString();
                }

                if (line.Length == MaxProtocolLineChars)
                {
                    throw new InvalidDataException("Codex returned an oversized app-server message.");
                }

                line.Append(character);
            }

            return line.Length == 0 ? null : line.ToString();
        }
    }

    private sealed class AppServerDiagnostics
    {
        private string? lastLine;

        public string? LastLine => Volatile.Read(ref lastLine);
        public void Record(string line) => Volatile.Write(ref lastLine, line);
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
