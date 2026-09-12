using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public async Task ActivityUpdatePublishesObservedAccountUsage()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var fiveHour = new AllowanceWindow(24, TimeSpan.FromHours(5), observedAt.AddHours(3));
        var weekly = new AllowanceWindow(61, TimeSpan.FromDays(7), observedAt.AddDays(4));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [weekly, fiveHour],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 123_456_789,
                    TodayTokens: 987_654,
                    LatestDailyBucketDate: DateOnly.FromDateTime(observedAt.LocalDateTime))),
            Local: null));

        Assert.Equal("Plus · Codex", presentation.Popup.AccountStatus);
        Assert.Equal("76% left", presentation.Popup.FiveHour.RemainingText);
        Assert.Equal("39% left", presentation.Popup.Weekly.RemainingText);
        Assert.Equal("123.46M tokens", presentation.Popup.LifetimeTokens);
        Assert.Equal("987.7K tokens", presentation.Popup.TodayTokens);
        Assert.Equal($"Updated {observedAt.LocalDateTime.ToString("t", CultureInfo.InvariantCulture)}", presentation.Popup.UpdatedText);
    }

    [Fact]
    public async Task RoutineUpdateRetainsSameDayActivity()
    {
        var activityObservedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var allowanceObservedAt = activityObservedAt.AddMinutes(1);
        var presentation = await PublishAsync(
            new UsageObservations(
                new AccountUsageObservation(
                    activityObservedAt,
                    [new AllowanceWindow(24, TimeSpan.FromHours(5), activityObservedAt.AddHours(3))],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.Observed(
                        LifetimeTokens: 123_456_789,
                        TodayTokens: 987_654,
                        LatestDailyBucketDate: DateOnly.FromDateTime(activityObservedAt.LocalDateTime))),
                Local: null),
            new UsageObservations(
                new AccountUsageObservation(
                    allowanceObservedAt,
                    [new AllowanceWindow(25, TimeSpan.FromHours(5), allowanceObservedAt.AddHours(3))],
                    "team",
                    "Codex",
                    new AccountActivityObservation.NotRequested()),
                Local: null));

        Assert.Equal("Team · Codex", presentation.Popup.AccountStatus);
        Assert.Equal("75% left", presentation.Popup.FiveHour.RemainingText);
        Assert.Equal("123.46M tokens", presentation.Popup.LifetimeTokens);
        Assert.Equal("987.7K tokens", presentation.Popup.TodayTokens);
        Assert.Equal(
            $"Updated {allowanceObservedAt.LocalDateTime.ToString("t", CultureInfo.InvariantCulture)}",
            presentation.Popup.UpdatedText);
    }

    [Fact]
    public async Task RoutineUpdateClearsRetainedTodayAcrossLocalDates()
    {
        var activityObservedAt = new DateTimeOffset(
            new DateTime(2026, 9, 7, 23, 59, 0, DateTimeKind.Local));
        var presentation = await PublishAsync(
            new UsageObservations(
                new AccountUsageObservation(
                    activityObservedAt,
                    [],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.Observed(
                        LifetimeTokens: 10_000,
                        TodayTokens: null,
                        LatestDailyBucketDate: new DateOnly(2026, 9, 6))),
                new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000)),
            new UsageObservations(
                new AccountUsageObservation(
                    activityObservedAt.AddMinutes(2),
                    [],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.NotRequested()),
                Local: null));

        Assert.Equal("12K tokens", presentation.Popup.LifetimeTokens);
        Assert.Equal("Unavailable", presentation.Popup.TodayTokens);
    }

    [Fact]
    public async Task ActivityUpdateUsesLocalTodayAndCompletesYesterdayLifetime()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: null,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 6))),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000)));

        Assert.Equal("2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal("12K tokens", presentation.Popup.LifetimeTokens);
    }

    [Fact]
    public async Task ActivityUpdatePrefersAccountTodayOverLocalToday()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: 500,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 7))),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000)));

        Assert.Equal("500 tokens", presentation.Popup.TodayTokens);
        Assert.Equal("10K tokens", presentation.Popup.LifetimeTokens);
    }

    [Fact]
    public async Task ActivityUpdateIgnoresLocalActivityFromAnotherDate()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: null,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 6))),
            new LocalUsageObservation(new DateOnly(2026, 9, 6), 2_000)));

        Assert.Equal("Unavailable", presentation.Popup.TodayTokens);
        Assert.Equal("10K tokens", presentation.Popup.LifetimeTokens);
    }

    [Fact]
    public async Task ActivityUpdateDoesNotCompleteLifetimeWhenAccountHistoryIncludesToday()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: null,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 7))),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000)));

        Assert.Equal("2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal("10K tokens", presentation.Popup.LifetimeTokens);
    }

    [Fact]
    public async Task ActivityUpdateKeepsAccountLifetimeWhenLocalCompletionOverflows()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: long.MaxValue,
                    TodayTokens: null,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 6))),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 1)));
        var expectedLifetime =
            $"{(long.MaxValue / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture)}B tokens";

        Assert.Equal("1 tokens", presentation.Popup.TodayTokens);
        Assert.Equal(expectedLifetime, presentation.Popup.LifetimeTokens);
    }

    [Fact]
    public async Task ActivityUpdateClearsPriorActivityWhenObservedValuesAreUnavailable()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(
            new UsageObservations(
                new AccountUsageObservation(
                    observedAt.AddMinutes(-1),
                    [],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.Observed(
                        LifetimeTokens: 10_000,
                        TodayTokens: 2_000,
                        LatestDailyBucketDate: new DateOnly(2026, 9, 7))),
                Local: null),
            new UsageObservations(
                new AccountUsageObservation(
                    observedAt,
                    [],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.Observed(
                        LifetimeTokens: null,
                        TodayTokens: null,
                        LatestDailyBucketDate: null)),
                Local: null));

        Assert.Equal("Unavailable", presentation.Popup.LifetimeTokens);
        Assert.Equal("Unavailable", presentation.Popup.TodayTokens);
    }

    [Fact]
    public async Task ActivityUpdateUsesShortestAndLongestAllowanceWindowsAsFallbacks()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [
                    new AllowanceWindow(20, TimeSpan.FromDays(1), observedAt.AddDays(1)),
                    new AllowanceWindow(10, TimeSpan.FromHours(1), observedAt.AddHours(1))
                ],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: null,
                    TodayTokens: null,
                    LatestDailyBucketDate: null)),
            Local: null));

        Assert.Equal(90, presentation.Tray.FiveHourRemaining);
        Assert.Equal(80, presentation.Tray.WeeklyRemaining);
    }

    [Fact]
    public async Task RoutineUpdateStartsWithoutActivity()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var presentation = await PublishAsync(new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [new AllowanceWindow(24, TimeSpan.FromHours(5), observedAt.AddHours(3))],
                "plus",
                "Codex",
                new AccountActivityObservation.NotRequested()),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000)));

        Assert.Equal("Unavailable", presentation.Popup.LifetimeTokens);
        Assert.Equal("Unavailable", presentation.Popup.TodayTokens);
    }

    private static async Task<UsagePresentation> PublishAsync(params UsageObservations[] observations)
    {
        var reader = new QueueObservationReader(observations);
        var settings = new DisabledActivationSettings();
        await using var updates = new UsageUpdates(
            reader,
            new NoOpActivationCommand(),
            settings,
            new ReconciliationTimeProvider(observations[^1].Account.ObservedAt),
            CultureInfo.InvariantCulture);

        UsagePresentation? presentation = null;
        foreach (var observation in observations)
        {
            presentation = observation.Account.Activity is AccountActivityObservation.NotRequested
                ? await updates.RefreshAsync()
                : await updates.RefreshWithActivityAsync();
        }

        return presentation!;
    }

    private sealed class QueueObservationReader(IEnumerable<UsageObservations> observations)
        : IUsageObservationReader
    {
        private readonly Queue<UsageObservations> observations = new(observations);

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = observations.Dequeue();
            var expected = observation.Account.Activity is AccountActivityObservation.NotRequested
                ? UsageObservationRequest.AllowanceWindows
                : UsageObservationRequest.AllowanceWindowsAndActivity;
            Assert.Equal(expected, request);
            return Task.FromResult(observation);
        }
    }

    private sealed class ReconciliationTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
