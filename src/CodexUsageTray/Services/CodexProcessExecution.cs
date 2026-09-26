using System.Diagnostics;
using System.Text;

namespace CodexUsageTray;

internal interface ICodexProcessExecution
{
    Task<TResult> ExchangeLinesAsync<TResult>(
        string codexArguments,
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken);

    Task<CodexProcessOutput> CaptureAsync(
        string codexArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface ICodexLineExchange
{
    string? LastStandardErrorLine { get; }
    Task WriteLineAsync(string line);
    ValueTask<string?> ReadLineAsync();
}

internal readonly record struct CodexProcessOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed class WindowsCodexProcessExecution : ICodexProcessExecution
{
    internal const int MaxProtocolLineChars = 1_048_576;
    private const int MaxCapturedStreamChars = 16_384;
    private const int MaxDiagnosticLineChars = 4_096;
    private static readonly Encoding Utf8WithoutByteOrderMark = new UTF8Encoding(false);

    public async Task<TResult> ExchangeLinesAsync<TResult>(
        string codexArguments,
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var codexPath = CodexCommandLocator.Find();
        timeoutCancellation.Token.ThrowIfCancellationRequested();
        using var process = Start(codexPath, codexArguments, redirectStandardInput: true);
        var lines = new WindowsCodexLineExchange(process, timeoutCancellation.Token);
        using var errorCancellation = new CancellationTokenSource();
        var standardError = lines.DrainStandardErrorAsync(errorCancellation.Token);
        var completed = false;
        try
        {
            var result = await exchange(lines).ConfigureAwait(false);
            timeoutCancellation.Token.ThrowIfCancellationRequested();
            completed = true;
            return result;
        }
        finally
        {
            if (!completed)
            {
                // Closing stdin can flush buffered data. Stop a failed exchange first
                // so cleanup cannot block on a child that no longer reads input.
                TryStopProcess(process);
            }

            await TryStopLineExchangeAsync(process, standardError).ConfigureAwait(false);
            errorCancellation.Cancel();
            try
            {
                await standardError.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (errorCancellation.IsCancellationRequested)
            {
                // A descendant may still hold the inherited stderr pipe open.
            }
        }
    }

    public async Task<CodexProcessOutput> CaptureAsync(
        string codexArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var codexPath = CodexCommandLocator.Find();
        timeoutCancellation.Token.ThrowIfCancellationRequested();
        using var process = Start(codexPath, codexArguments, redirectStandardInput: false);

        try
        {
            var standardOutput = ReadTailAsync(process.StandardOutput, timeoutCancellation.Token);
            var standardError = ReadTailAsync(process.StandardError, timeoutCancellation.Token);
            await Task.WhenAll(
                standardOutput,
                standardError,
                process.WaitForExitAsync(timeoutCancellation.Token)).ConfigureAwait(false);
            return new CodexProcessOutput(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch
        {
            TryStopProcess(process);
            throw;
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var tail = new char[MaxCapturedStreamChars];
        var buffer = new char[4096];
        var next = 0;
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            foreach (var character in buffer.AsSpan(0, read))
            {
                tail[next] = character;
                next = (next + 1) % tail.Length;
                count = Math.Min(count + 1, tail.Length);
            }
        }

        var start = count == tail.Length ? next : 0;
        return string.Create(count, (tail, start), static (result, state) =>
        {
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = state.tail[(state.start + index) % state.tail.Length];
            }
        });
    }

    private static Process Start(
        string codexPath,
        string codexArguments,
        bool redirectStandardInput)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /s /c \"\"{codexPath}\" {codexArguments}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8WithoutByteOrderMark,
            StandardErrorEncoding = Utf8WithoutByteOrderMark
        };
        if (redirectStandardInput)
        {
            startInfo.StandardInputEncoding = Utf8WithoutByteOrderMark;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the Codex CLI.");
    }

    private static async Task TryStopLineExchangeAsync(Process process, Task standardError)
    {
        try
        {
            process.StandardInput.Close();
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                // Drain final diagnostics, including a line without a newline. Bound the
                // wait because a descendant can hold the inherited stderr pipe open.
                await Task.WhenAll(process.WaitForExitAsync(cleanupCancellation.Token), standardError)
                    .WaitAsync(cleanupCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryStopProcess(process);
            }
        }
        catch
        {
            // The process may already have exited.
        }
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

    private sealed class WindowsCodexLineExchange(
        Process process,
        CancellationToken cancellationToken) : ICodexLineExchange
    {
        private string? lastStandardErrorLine;
        private readonly char[] lineCharacter = new char[1];
        private bool skipLineFeed;

        public string? LastStandardErrorLine => Volatile.Read(ref lastStandardErrorLine);

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
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                if (character is '\r' or '\n')
                {
                    skipLineFeed = character == '\r';
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

        public async Task DrainStandardErrorAsync(CancellationToken errorCancellationToken)
        {
            var buffer = new char[4096];
            var line = new char[MaxDiagnosticLineChars];
            var next = 0;
            var count = 0;
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer.AsMemory(), errorCancellationToken)
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
                    Volatile.Write(ref lastStandardErrorLine, value);
                }
            }
        }
    }
}
