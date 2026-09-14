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
        var completed = false;
        try
        {
            process.ErrorDataReceived += lines.RecordStandardError;
            process.BeginErrorReadLine();
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

            await TryStopLineExchangeAsync(process).ConfigureAwait(false);
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
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutCancellation.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeoutCancellation.Token);
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

    private static async Task TryStopLineExchangeAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                // Include stderr completion in the grace period: an exited command
                // can leave a child holding its inherited stderr pipe open.
                await process.WaitForExitAsync(cleanupCancellation.Token).ConfigureAwait(false);
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

        public string? LastStandardErrorLine => Volatile.Read(ref lastStandardErrorLine);

        public async Task WriteLineAsync(string line)
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<string?> ReadLineAsync() =>
            process.StandardOutput.ReadLineAsync(cancellationToken);

        public void RecordStandardError(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            Volatile.Write(ref lastStandardErrorLine, eventArgs.Data);
        }
    }
}
