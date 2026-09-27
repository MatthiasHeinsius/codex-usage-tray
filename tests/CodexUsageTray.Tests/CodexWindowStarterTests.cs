using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class CodexWindowStarterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendHiUsesEphemeralThreadOnAppServer(bool completionBeforeTurnResponse)
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":5,"result":{"account":{"email":"user@example.com"}}}""");
        processes.EnqueueLine("""{"method":"thread/started","params":{"thread":{"id":"thr_1"}}}""");
        processes.EnqueueLine("""{"id":2,"result":{"thread":{"id":"thr_1","ephemeral":true}}}""");
        var started = """{"id":3,"result":{"turn":{"id":"turn_1","status":"inProgress"}}}""";
        var completed = """{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"completed"}}}""";
        processes.EnqueueLine(completionBeforeTurnResponse ? completed : started);
        processes.EnqueueLine(completionBeforeTurnResponse ? started : completed);
        processes.EnqueueLine("""{"id":4,"result":{"status":"unsubscribed"}}""");

        await new CodexWindowStarter(processes).SendHiAsync("USER@example.com", CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(2), processes.ExchangeTimeout);
        var requests = processes.WrittenLines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal("initialize", requests[0].RootElement.GetProperty("method").GetString());
            Assert.Equal("account/read", requests[2].RootElement.GetProperty("method").GetString());
            var thread = requests[3].RootElement;
            Assert.Equal("thread/start", thread.GetProperty("method").GetString());
            var options = thread.GetProperty("params");
            Assert.True(options.GetProperty("ephemeral").GetBoolean());
            Assert.Equal("gpt-5.6-luna", options.GetProperty("model").GetString());
            Assert.Equal("read-only", options.GetProperty("sandbox").GetString());
            Assert.Equal("never", options.GetProperty("approvalPolicy").GetString());
            var turn = requests[4].RootElement;
            Assert.Equal("turn/start", turn.GetProperty("method").GetString());
            Assert.Equal("Hi", turn.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
            Assert.Equal("thread/unsubscribe", requests[5].RootElement.GetProperty("method").GetString());
        }
        finally
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }
        }
    }

    [Fact]
    public async Task SendHiStopsWhenTheAccountChangedAfterTheUsageRead()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":5,"result":{"account":{"email":"second@example.com"}}}""");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexWindowStarter(processes).SendHiAsync("first@example.com", CancellationToken.None));

        Assert.Equal("Codex account changed before allowance activation.", failure.Message);
        Assert.DoesNotContain(processes.WrittenLines, line => line.Contains("thread/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendHiRejectsStoredThreadWithoutSendingTurn()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"thread":{"id":"thr_1","ephemeral":false}}}""");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexWindowStarter(processes).SendHiAsync(null, CancellationToken.None));

        Assert.Equal("Codex did not start an ephemeral activation thread.", failure.Message);
        Assert.DoesNotContain(processes.WrittenLines, line => line.Contains("turn/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendHiReportsTurnFailure()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"thread":{"id":"thr_1","ephemeral":true}}}""");
        processes.EnqueueLine("""{"id":3,"result":{"turn":{"id":"turn_1","status":"inProgress"}}}""");
        processes.EnqueueLine("""{"method":"turn/completed","params":{"threadId":"thr_1","turn":{"id":"turn_1","status":"failed","error":{"message":"quota unavailable"}}}}""");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexWindowStarter(processes).SendHiAsync(null, CancellationToken.None));

        Assert.Equal("The automatic Codex request failed: quota unavailable", failure.Message);
    }

    [Fact]
    public async Task SendHiMapsTimeoutAndPreservesCallerCancellation()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueReadFailure(new OperationCanceledException());
        var starter = new CodexWindowStarter(processes);

        var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            starter.SendHiAsync(null, CancellationToken.None));
        Assert.Equal("The automatic Codex request did not finish within two minutes.", timeout.Message);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        processes.EnqueueReadFailure(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.SendHiAsync(null, cancellation.Token));
        Assert.Equal(cancellation.Token, processes.ExchangeCancellationToken);
    }
}
