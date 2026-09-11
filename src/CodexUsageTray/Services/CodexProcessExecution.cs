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
        var codexPath = CodexCommandLocator.Find();
        using var process = Start(codexPath, codexArguments, redirectStandardInput: true);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var lines = new WindowsCodexLineExchange(process, timeoutCancellation.Token);
        process.ErrorDataReceived += lines.RecordStandardError;
        process.BeginErrorReadLine();

        try
        {
            return await exchange(lines).ConfigureAwait(false);
        }
        finally
        {
            TryStopLineExchange(process);
        }
    }

    public async Task<CodexProcessOutput> CaptureAsync(
        string codexArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var codexPath = CodexCommandLocator.Find();
        using var process = Start(codexPath, codexArguments, redirectStandardInput: false);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryStopCapturedProcess(process);
            throw;
        }

        return new CodexProcessOutput(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
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

    private static void TryStopLineExchange(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(500))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }

    private static void TryStopCapturedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }

    private sealed class WindowsCodexLineExchange(
        Process process,
        CancellationToken readCancellation) : ICodexLineExchange
    {
        private readonly List<string> standardErrorLines = [];

        public string? LastStandardErrorLine
        {
            get
            {
                lock (standardErrorLines)
                {
                    return standardErrorLines.LastOrDefault();
                }
            }
        }

        public async Task WriteLineAsync(string line)
        {
            await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        public ValueTask<string?> ReadLineAsync() =>
            process.StandardOutput.ReadLineAsync(readCancellation);

        public void RecordStandardError(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            lock (standardErrorLines)
            {
                standardErrorLines.Add(eventArgs.Data);
            }
        }
    }
}
