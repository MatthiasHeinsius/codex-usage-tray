namespace CodexUsageTray.Tests;

public sealed partial class CodexUsageObservationReaderTests
{
    [Theory]
    [InlineData("timeout")]
    [InlineData("broken pipe")]
    [InlineData("closed stream")]
    public async Task SignInTransportFailureStopsRecoveryAndAllowsLaterUsageReads(string failureKind)
    {
        var processes = new ScriptedCodexProcessExecution();
        EnqueueExpiredReadAndFailedRefresh(processes);
        EnqueueSignInStart(processes);
        switch (failureKind)
        {
            case "timeout":
                // The process deadline expires independently of the caller's cancellation token.
                processes.EnqueueReadFailure(new OperationCanceledException(new CancellationToken(canceled: true)));
                break;
            case "broken pipe":
                processes.EnqueueReadFailure(new IOException("The pipe has been ended."));
                break;
            case "closed stream":
                processes.EnqueueLine(null);
                break;
        }
        var interaction = new RecordingAuthenticationInteraction(confirm: true);
        var reader = new CodexUsageObservationReader(processes, interaction);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync(UsageObservationRequest.AllowanceWindows, TestContext.Current.CancellationToken));

        Assert.Equal(CodexAuthenticationRecovery.FailureMessage, failure.Message);
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromMinutes(5), processes.ExchangeTimeout);
        Assert.Equal(1, CountUsageReads(processes));
        Assert.Single(interaction.SignInPages);

        // An expired request must not reopen the browser after the interrupted sign-in.
        EnqueueExpiredReadAndFailedRefresh(processes);
        var repeatedFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync(UsageObservationRequest.AllowanceWindows, TestContext.Current.CancellationToken));

        Assert.Equal(CodexAuthenticationRecovery.FailureMessage, repeatedFailure.Message);
        Assert.Equal(2, CountUsageReads(processes));
        Assert.Single(interaction.SignInPages);
        Assert.Single(processes.WrittenLines, line => line.Contains("\"account/login/start\"", StringComparison.Ordinal));

        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}}""");

        var recovered = await reader.ReadAsync(
            UsageObservationRequest.AllowanceWindows, TestContext.Current.CancellationToken);

        Assert.Equal(25, Assert.Single(recovered.Account.AllowanceWindows).UsedPercent);
        Assert.Equal(3, CountUsageReads(processes));
        Assert.Single(interaction.SignInPages);
    }

    [Theory]
    [InlineData("""{"id":2}""")]
    [InlineData("""{"id":2,"result":{}}""")]
    [InlineData("""{"id":2,"result":{"loginId":" "}}""")]
    [InlineData("""{"id":2,"result":{"loginId":"login-1"}}""")]
    [InlineData("""{"id":2,"result":{"loginId":"login-1","authUrl":"not a URL"}}""")]
    [InlineData("""{"id":2,"result":{"loginId":"login-1","authUrl":"http://chatgpt.com/auth"}}""")]
    public async Task InvalidSignInDetailsNeverOpenTheBrowser(string response)
    {
        var processes = new ScriptedCodexProcessExecution();
        EnqueueExpiredReadAndFailedRefresh(processes);
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine(response);
        var interaction = new RecordingAuthenticationInteraction(confirm: true);
        var reader = new CodexUsageObservationReader(processes, interaction);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync(UsageObservationRequest.AllowanceWindows, TestContext.Current.CancellationToken));

        Assert.Equal(CodexAuthenticationRecovery.FailureMessage, failure.Message);
        Assert.Empty(interaction.SignInPages);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"method":"other"}""")]
    [InlineData("""{"method":"account/login/completed"}""")]
    [InlineData("""{"method":"account/login/completed","params":{}}""")]
    [InlineData("""{"method":"account/login/completed","params":{"loginId":"other-login","success":true}}""")]
    [InlineData("""{"method":"account/login/completed","params":{"loginId":"login-1"}}""")]
    [InlineData("""{"method":"account/login/completed","params":{"loginId":"login-1","success":"true"}}""")]
    public async Task SignInWaitsForAValidCompletionForItsOwnLogin(string notification)
    {
        var processes = new ScriptedCodexProcessExecution();
        EnqueueExpiredReadAndFailedRefresh(processes);
        EnqueueSignInStart(processes);
        processes.EnqueueLine(notification);
        var usageReadsAtCompletion = 0;
        processes.EnqueueRead(() =>
        {
            usageReadsAtCompletion = CountUsageReads(processes);
            return """{"method":"account/login/completed","params":{"loginId":"login-1","success":true}}""";
        });
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}}""");
        var interaction = new RecordingAuthenticationInteraction(confirm: true);
        var reader = new CodexUsageObservationReader(processes, interaction);

        var observation = await reader.ReadAsync(
            UsageObservationRequest.AllowanceWindows, TestContext.Current.CancellationToken);

        Assert.Equal(25, Assert.Single(observation.Account.AllowanceWindows).UsedPercent);
        Assert.Equal(1, usageReadsAtCompletion);
        Assert.Equal(2, CountUsageReads(processes));
        Assert.Single(interaction.SignInPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringAuthenticationStopsRecovery(bool duringSignIn)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var processes = new ScriptedCodexProcessExecution();
        if (duringSignIn)
        {
            EnqueueExpiredReadAndFailedRefresh(processes);
            EnqueueSignInStart(processes);
        }
        else
        {
            EnqueueExpiredRead(processes);
            processes.EnqueueLine("""{"id":1,"result":{}}""");
        }
        processes.EnqueueRead(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var interaction = new RecordingAuthenticationInteraction(confirm: true);
        var reader = new CodexUsageObservationReader(processes, interaction);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(UsageObservationRequest.AllowanceWindows, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(cancellation.Token, processes.ExchangeCancellationToken);
        Assert.Equal(1, CountUsageReads(processes));
        Assert.Equal(duringSignIn ? 1 : 0, interaction.SignInPages.Count);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 3)]
    public async Task UnsuccessfulRecoveryReportsExpiredSignInWithoutLooping(
        bool refreshReportsSuccess, bool signInReportsSuccess, int expectedUsageReads)
    {
        var processes = new ScriptedCodexProcessExecution();
        if (refreshReportsSuccess)
        {
            EnqueueExpiredRead(processes);
            processes.EnqueueLine("""{"id":1,"result":{}}""");
            processes.EnqueueLine("""{"id":2,"result":{"account":{"type":"chatgpt"}}}""");
            EnqueueExpiredRead(processes);
        }
        else
        {
            EnqueueExpiredReadAndFailedRefresh(processes);
        }
        EnqueueSignInStart(processes);
        processes.EnqueueLine(signInReportsSuccess
            ? """{"method":"account/login/completed","params":{"loginId":"login-1","success":true}}"""
            : """{"method":"account/login/completed","params":{"loginId":"login-1","success":false}}""");
        if (signInReportsSuccess)
        {
            EnqueueExpiredRead(processes);
        }
        var interaction = new RecordingAuthenticationInteraction(confirm: true);
        var reader = new CodexUsageObservationReader(processes, interaction);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync(UsageObservationRequest.AllowanceWindows, TestContext.Current.CancellationToken));

        Assert.Equal(CodexAuthenticationRecovery.FailureMessage, failure.Message);
        Assert.Single(interaction.SignInPages);
        Assert.Equal(expectedUsageReads, CountUsageReads(processes));
        Assert.Single(processes.WrittenLines, line => line.Contains("\"account/read\"", StringComparison.Ordinal));
    }

    private static void EnqueueExpiredRead(ScriptedCodexProcessExecution processes)
    {
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"error":{"message":"Provided authentication token is expired"}}""");
    }

    private static void EnqueueSignInStart(ScriptedCodexProcessExecution processes)
    {
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"loginId":"login-1","authUrl":"https://chatgpt.com/auth"}}""");
    }

    private static int CountUsageReads(ScriptedCodexProcessExecution processes) =>
        processes.WrittenLines.Count(line => line.Contains("\"account/rateLimits/read\"", StringComparison.Ordinal));
}
