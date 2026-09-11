using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class CodexUsageObservationReaderTests
{
    [Fact]
    public async Task ReadUsesLineExchangeForTheAppServerConversation()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine("""
            {"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}}
            """);
        var reader = new CodexUsageObservationReader(processes);

        var observations = await reader.ReadAsync(
            UsageObservationRequest.AllowanceWindows,
            CancellationToken.None);

        Assert.Equal("app-server --stdio", processes.ExchangeArguments);
        Assert.Equal(TimeSpan.FromSeconds(45), processes.ExchangeTimeout);
        Assert.Equal(CancellationToken.None, processes.ExchangeCancellationToken);
        Assert.Collection(
            processes.WrittenLines,
            line => Assert.Equal(InitializeRequest(), line),
            line => Assert.Equal(JsonSerializer.Serialize(new { method = "initialized" }), line),
            line => Assert.Equal(Request(2, "account/rateLimits/read"), line));
        var window = Assert.Single(observations.Account.AllowanceWindows);
        Assert.Equal(25, window.UsedPercent);
        Assert.IsType<AccountActivityObservation.NotRequested>(observations.Account.Activity);
    }

    [Fact]
    public async Task ReadRequestsActivityThroughTheSameLineExchange()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"result":{}}""");
        processes.EnqueueLine(JsonSerializer.Serialize(new
        {
            id = 3,
            result = new
            {
                summary = new { lifetimeTokens = 50 },
                dailyUsageBuckets = new[]
                {
                    new
                    {
                        startDate = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        tokens = 20
                    },
                    new
                    {
                        startDate = today.AddDays(1)
                            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        tokens = 20
                    }
                }
            }
        }));
        processes.EnqueueLine("""
            {"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}}
            """);
        var reader = new CodexUsageObservationReader(processes);

        var observations = await reader.ReadAsync(
            UsageObservationRequest.AllowanceWindowsAndActivity,
            CancellationToken.None);

        Assert.Collection(
            processes.WrittenLines,
            line => Assert.Equal(InitializeRequest(), line),
            line => Assert.Equal(JsonSerializer.Serialize(new { method = "initialized" }), line),
            line => Assert.Equal(Request(2, "account/rateLimits/read"), line),
            line => Assert.Equal(Request(3, "account/usage/read"), line));
        var activity = Assert.IsType<AccountActivityObservation.Observed>(observations.Account.Activity);
        Assert.Equal(50, activity.LifetimeTokens);
        Assert.Equal(20, activity.TodayTokens);
    }

    [Fact]
    public async Task ReadMapsLineExchangeTimeoutWithLatestDiagnostic()
    {
        var processes = new ScriptedCodexProcessExecution
        {
            LastStandardErrorLine = "latest diagnostic"
        };
        processes.EnqueueReadFailure(new OperationCanceledException());
        var reader = new CodexUsageObservationReader(processes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(UsageObservationRequest.AllowanceWindows, CancellationToken.None));

        Assert.Equal(
            "Codex did not return usage data within 45 seconds. latest diagnostic",
            failure.Message);
    }

    [Fact]
    public async Task ReadPreservesProtocolErrors()
    {
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueLine("""{"id":1,"error":{"message":"unsupported"}}""");
        var reader = new CodexUsageObservationReader(processes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(UsageObservationRequest.AllowanceWindows, CancellationToken.None));

        Assert.Equal("Codex returned an error: unsupported", failure.Message);
    }

    [Fact]
    public async Task ReadMapsNonProtocolFailuresWithLatestDiagnostic()
    {
        var readFailure = new IOException("read failed");
        var processes = new ScriptedCodexProcessExecution
        {
            LastStandardErrorLine = "latest diagnostic"
        };
        processes.EnqueueReadFailure(readFailure);
        var reader = new CodexUsageObservationReader(processes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(UsageObservationRequest.AllowanceWindows, CancellationToken.None));

        Assert.Equal("Could not read Codex usage: latest diagnostic", failure.Message);
        Assert.Same(readFailure, failure.InnerException);
    }

    [Fact]
    public async Task ReadPreservesAndForwardsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var processes = new ScriptedCodexProcessExecution();
        processes.EnqueueReadFailure(new OperationCanceledException(cancellation.Token));
        var reader = new CodexUsageObservationReader(processes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(UsageObservationRequest.AllowanceWindows, cancellation.Token));

        Assert.Equal(cancellation.Token, processes.ExchangeCancellationToken);
    }

    [Fact]
    public void ParseAccountObservationSelectsAndNormalizesCodexAllowanceWindows()
    {
        const string json = """
            {
              "rateLimits": { "primary": { "usedPercent": 99, "windowDurationMins": 300 } },
              "rateLimitsByLimitId": {
                "other": { "primary": { "usedPercent": 80, "windowDurationMins": 60 } },
                "codex": {
                  "primary": { "usedPercent": 25, "windowDurationMins": 10080 },
                  "secondary": { "usedPercent": 10, "windowDurationMins": 300 }
                }
              }
            }
            """;
        using var response = JsonDocument.Parse(json);
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));

        var observation = CodexUsageObservationReader.ParseAccountObservation(
            response.RootElement,
            usageResponse: null,
            observedAt);

        Assert.Equal(observedAt, observation.ObservedAt);
        Assert.Collection(
            observation.AllowanceWindows,
            window =>
            {
                Assert.Equal(25, window.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(7), window.Duration);
            },
            window =>
            {
                Assert.Equal(10, window.UsedPercent);
                Assert.Equal(TimeSpan.FromHours(5), window.Duration);
            });
        Assert.IsType<AccountActivityObservation.NotRequested>(observation.Activity);
    }

    [Fact]
    public void ParseAccountObservationTranslatesAccountActivity()
    {
        const string limitsJson = """
            {
              "rateLimits": {
                "limitName": "Codex",
                "planType": "plus",
                "primary": { "usedPercent": 24, "windowDurationMins": 300, "resetsAt": 1788780000 }
              }
            }
            """;
        const string usageJson = """
            {
              "summary": { "lifetimeTokens": 123456789 },
              "dailyUsageBuckets": [
                { "startDate": "2026-09-06", "tokens": 10 },
                { "startDate": "2026-09-07", "tokens": 987654 }
              ]
            }
            """;
        using var limits = JsonDocument.Parse(limitsJson);
        using var usage = JsonDocument.Parse(usageJson);
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));

        var observation = CodexUsageObservationReader.ParseAccountObservation(
            limits.RootElement,
            usage.RootElement,
            observedAt);

        Assert.Equal("plus", observation.Plan);
        Assert.Equal("Codex", observation.LimitName);
        var activity = Assert.IsType<AccountActivityObservation.Observed>(observation.Activity);
        Assert.Equal(123_456_789, activity.LifetimeTokens);
        Assert.Equal(987_654, activity.TodayTokens);
        Assert.Equal(new DateOnly(2026, 9, 7), activity.LatestDailyBucketDate);
    }

    private static string InitializeRequest() => JsonSerializer.Serialize(new
    {
        id = 1,
        method = "initialize",
        @params = new
        {
            clientInfo = new
            {
                name = "codex-usage-tray",
                title = "Codex Usage Tray",
                version = typeof(CodexUsageObservationReader).Assembly
                    .GetName().Version?.ToString(3) ?? "unknown"
            },
            capabilities = new { experimentalApi = true }
        }
    });

    private static string Request(int id, string method) =>
        JsonSerializer.Serialize(new { id, method, @params = (object?)null });
}
