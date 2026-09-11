namespace CodexUsageTray.Tests;

internal sealed class ScriptedCodexProcessExecution : ICodexProcessExecution
{
    private readonly Queue<object?> exchangeResponses = [];

    public string? ExchangeArguments { get; private set; }
    public TimeSpan ExchangeTimeout { get; private set; }
    public CancellationToken ExchangeCancellationToken { get; private set; }
    public List<string> WrittenLines { get; } = [];
    public string? LastStandardErrorLine { get; set; }

    public string? CaptureArguments { get; private set; }
    public TimeSpan CaptureTimeout { get; private set; }
    public CancellationToken CaptureCancellationToken { get; private set; }
    public CodexProcessOutput CapturedOutput { get; set; }
    public Exception? CaptureFailure { get; set; }

    public void EnqueueLine(string? line) => exchangeResponses.Enqueue(line);

    public void EnqueueReadFailure(Exception exception) => exchangeResponses.Enqueue(exception);

    public async Task<TResult> ExchangeLinesAsync<TResult>(
        string codexArguments,
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken)
    {
        ExchangeArguments = codexArguments;
        ExchangeTimeout = timeout;
        ExchangeCancellationToken = cancellationToken;

        return await exchange(new ScriptedLineExchange(this));
    }

    public Task<CodexProcessOutput> CaptureAsync(
        string codexArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CaptureArguments = codexArguments;
        CaptureTimeout = timeout;
        CaptureCancellationToken = cancellationToken;
        return CaptureFailure is { } failure
            ? Task.FromException<CodexProcessOutput>(failure)
            : Task.FromResult(CapturedOutput);
    }

    private sealed class ScriptedLineExchange(ScriptedCodexProcessExecution owner) : ICodexLineExchange
    {
        public string? LastStandardErrorLine => owner.LastStandardErrorLine;

        public Task WriteLineAsync(string line)
        {
            owner.WrittenLines.Add(line);
            return Task.CompletedTask;
        }

        public ValueTask<string?> ReadLineAsync()
        {
            if (!owner.exchangeResponses.TryDequeue(out var response))
            {
                return ValueTask.FromResult<string?>(null);
            }

            return response is Exception failure
                ? ValueTask.FromException<string?>(failure)
                : ValueTask.FromResult((string?)response);
        }
    }
}
