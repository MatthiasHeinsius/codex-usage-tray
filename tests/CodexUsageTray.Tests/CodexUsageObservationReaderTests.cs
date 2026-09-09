using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class CodexUsageObservationReaderTests
{
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
}
