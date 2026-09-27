namespace CodexUsageTray.Tests;

public sealed partial class CodexUsageObservationReaderTests
{
    [Fact]
    public async Task FirstLaunchStartsBrowserSignInWithoutTryingTokenRefresh()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"error":{"message":"codex account authentication required to read rate limits"}}""");
        processes.EnqueueLine("""{"id":4,"result":{"account":null,"requiresOpenaiAuth":true}}""");
        EnqueueSignInStart(processes);
        processes.EnqueueLine("""{"method":"account/login/completed","params":{"loginId":"login-1","success":true}}""");
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300}}}}""");
        EnqueueAccountDetails(processes);
        var interaction = new RecordingAuthenticationInteraction(confirm: true);
        var reader = new CodexUsageObservationReader(processes, interaction);

        var observation = await reader.ReadAsync(
            UsageObservationRequest.AllowanceWindows,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, Assert.Single(observation.Account.AllowanceWindows).UsedPercent);
        Assert.Single(interaction.SignInPages);
        Assert.DoesNotContain(processes.WrittenLines,
            line => line.Contains("\"refreshToken\":true", StringComparison.Ordinal));
        Assert.Equal(2, CountUsageReads(processes));
    }

    [Theory]
    [InlineData("codex account authentication required to read rate limits")]
    [InlineData("Not logged in")]
    public async Task MissingAccountWithoutInteractionExplainsHowToSignIn(string rateLimitError)
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 2,
            error = new { message = rateLimitError }
        }));
        processes.EnqueueLine("""{"id":4,"result":{"account":null,"requiresOpenaiAuth":true}}""");
        var reader = new CodexUsageObservationReader(processes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync(UsageObservationRequest.AllowanceWindows, CancellationToken.None));

        Assert.Equal(CodexAuthenticationRecovery.SignInRequiredMessage, failure.Message);
    }

    [Fact]
    public async Task NullAccountAlsoTriggersSignInWhenRateLimitReadSucceeds()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"result":{"rateLimits":{}}}""");
        processes.EnqueueLine("""{"id":4,"result":{"account":null,"requiresOpenaiAuth":true}}""");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexUsageObservationReader(processes)
                .ReadAsync(UsageObservationRequest.AllowanceWindows, CancellationToken.None));

        Assert.Equal(CodexAuthenticationRecovery.SignInRequiredMessage, failure.Message);
    }

    [Fact]
    public async Task DecliningFirstLaunchSignInDoesNotPromptEveryMinute()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"error":{"message":"codex account authentication required to read rate limits"}}""");
        EnqueueSignInStart(processes);
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""{"id":2,"error":{"message":"codex account authentication required to read rate limits"}}""");
        var interaction = new RecordingAuthenticationInteraction(confirm: false);
        var reader = new CodexUsageObservationReader(processes, interaction);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.ReadAsync(UsageObservationRequest.AllowanceWindows, CancellationToken.None));
            Assert.Equal(CodexAuthenticationRecovery.SignInRequiredMessage, failure.Message);
        }

        Assert.Single(interaction.SignInPages);
        Assert.Single(processes.WrittenLines,
            line => line.Contains("\"account/login/start\"", StringComparison.Ordinal));
    }

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
        EnqueueAccountDetails(processes);

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
    [InlineData("""{"id":2,"result":{"loginId":"login-1","authUrl":"https://chatgpt.com.evil.test/auth"}}""")]
    [InlineData("""{"id":2,"result":{"loginId":"login-1","authUrl":"https://evil.test/auth"}}""")]
    [InlineData("""{"id":2,"result":{"loginId":"login-1","authUrl":"https://chatgpt.com:444/auth"}}""")]
    [InlineData("""{"id":2,"result":{"loginId":"login-1","authUrl":"https://user@chatgpt.com/auth"}}""")]
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
        EnqueueAccountDetails(processes);
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
        Assert.Single(processes.WrittenLines, line => line.Contains("\"refreshToken\":true", StringComparison.Ordinal));
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
