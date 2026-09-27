namespace CodexUsageTray.Tests;

internal sealed class ScriptedCodexProcessExecution : ICodexProcessExecution
{
    private readonly Queue<object?> exchangeResponses = [];

    public TimeSpan ExchangeTimeout { get; private set; }
    public CancellationToken ExchangeCancellationToken { get; private set; }
    public List<string> WrittenLines { get; } = [];
    public string? LastStandardErrorLine { get; set; }

    public void EnqueueLine(string? line) => exchangeResponses.Enqueue(line);

    public void EnqueueReadFailure(Exception exception) => exchangeResponses.Enqueue(exception);

    public void EnqueueRead(Func<string?> read) => exchangeResponses.Enqueue(read);

    public async Task<TResult> ExchangeLinesAsync<TResult>(
        TimeSpan timeout,
        Func<ICodexLineExchange, Task<TResult>> exchange,
        CancellationToken cancellationToken)
    {
        ExchangeTimeout = timeout;
        ExchangeCancellationToken = cancellationToken;

        return await exchange(new ScriptedLineExchange(this));
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
                : ValueTask.FromResult(response is Func<string?> read ? read() : (string?)response);
        }
    }
}
