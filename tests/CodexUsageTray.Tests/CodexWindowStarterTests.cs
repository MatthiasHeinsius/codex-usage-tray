using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class CodexWindowStarterTests
{
    [Theory]
    [InlineData(false, true, true, false, "gpt-6-luna")]
    [InlineData(true, true, true, false, "gpt-6-luna")]
    [InlineData(false, false, true, false, "gpt-5.6-luna")]
    [InlineData(false, false, false, false, "gpt-6-sol")]
    [InlineData(false, true, true, true, "gpt-6-luna")]
    public async Task SendHiUsesAnAvailableModelInAnEphemeralThread(
        bool completionBeforeTurnResponse, bool sixAvailable, bool olderLunaAvailable,
        bool sixOnSecondPage, string expectedModel)
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":5,"result":{"account":{"email":"user@example.com"}}}""");
        EnqueueModels(processes, sixAvailable, olderLunaAvailable, sixOnSecondPage);
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
            Assert.Equal("model/list", requests[3].RootElement.GetProperty("method").GetString());
            var threadIndex = sixOnSecondPage ? 5 : 4;
            if (sixOnSecondPage)
            {
                Assert.Equal("model/list", requests[4].RootElement.GetProperty("method").GetString());
                Assert.Equal("next", requests[4].RootElement.GetProperty("params").GetProperty("cursor").GetString());
            }
            var thread = requests[threadIndex].RootElement;
            Assert.Equal("thread/start", thread.GetProperty("method").GetString());
            var options = thread.GetProperty("params");
            Assert.True(options.GetProperty("ephemeral").GetBoolean());
            Assert.Equal(expectedModel, options.GetProperty("model").GetString());
            Assert.Equal("read-only", options.GetProperty("sandbox").GetString());
            Assert.Equal("never", options.GetProperty("approvalPolicy").GetString());
            var turn = requests[threadIndex + 1].RootElement;
            Assert.Equal("turn/start", turn.GetProperty("method").GetString());
            Assert.Equal("Hi", turn.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
            Assert.Equal("thread/unsubscribe", requests[threadIndex + 2].RootElement.GetProperty("method").GetString());
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
        EnqueueModels(processes);
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
        EnqueueModels(processes);
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

    [Fact]
    public async Task SendHiDoesNotStartAThreadWhenNoModelIsAvailable()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":6,"result":{"data":[]}}""");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexWindowStarter(processes).SendHiAsync(null, CancellationToken.None));

        Assert.Equal("Codex did not offer an activation model.", failure.Message);
        Assert.DoesNotContain(processes.WrittenLines, line => line.Contains("thread/start", StringComparison.Ordinal));
    }

    private static void EnqueueModels(
        ScriptedCodexProcessExecution processes, bool sixAvailable = true,
        bool olderLunaAvailable = true, bool sixOnSecondPage = false)
    {
        var firstPage = new List<object> { new { model = "gpt-6-sol", isDefault = true } };
        if (olderLunaAvailable)
        {
            firstPage.Add(new { model = "gpt-5.6-luna", isDefault = false });
        }
        if (sixAvailable && !sixOnSecondPage)
        {
            firstPage.Add(new { model = "gpt-6-luna", isDefault = false });
        }
        processes.EnqueueLine(JsonSerializer.Serialize(new
        {
            id = 6,
            result = new { data = firstPage, nextCursor = sixOnSecondPage ? "next" : null }
        }));
        if (sixOnSecondPage)
        {
            processes.EnqueueLine("""{"id":6,"result":{"data":[{"model":"gpt-6-luna","isDefault":false}],"nextCursor":null}}""");
        }
    }
}
