namespace CodexUsageTray.Tests;

public sealed partial class UsageSnapshotsTests
{
    [Fact]
    public void RefreshWithActivityPublishesObservedAccountUsage()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var fiveHour = new AllowanceWindow(24, TimeSpan.FromHours(5), observedAt.AddHours(3));
        var weekly = new AllowanceWindow(61, TimeSpan.FromDays(7), observedAt.AddDays(4));
        var account = new AccountUsageObservation(
            observedAt,
            [weekly, fiveHour],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: 123_456_789,
                TodayTokens: 987_654,
                LatestDailyBucketDate: DateOnly.FromDateTime(observedAt.LocalDateTime)));

        var snapshot = UsageSnapshotFixture.Create(account);

        Assert.Equal(observedAt, snapshot.AllowanceObservedAt);
        Assert.Equal(observedAt, snapshot.ActivityObservedAt);
        Assert.Equal(fiveHour, snapshot.FiveHour);
        Assert.Equal(weekly, snapshot.Weekly);
        Assert.Equal(123_456_789, snapshot.LifetimeTokens);
        Assert.Equal(987_654, snapshot.TodayTokens);
        Assert.Equal("plus", snapshot.Plan);
        Assert.Equal("Codex", snapshot.LimitName);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void RoutineRefreshRetainsSameDayActivity()
    {
        var firstObservationAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var secondObservationAt = firstObservationAt.AddMinutes(1);
        var snapshots = UsageSnapshotFixture.Create(
            new UsageObservations(
                new AccountUsageObservation(
                    firstObservationAt,
                    [new AllowanceWindow(24, TimeSpan.FromHours(5), firstObservationAt.AddHours(3))],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.Observed(
                        LifetimeTokens: 123_456_789,
                        TodayTokens: 987_654,
                        LatestDailyBucketDate: DateOnly.FromDateTime(firstObservationAt.LocalDateTime))),
                Local: null),
            new UsageObservations(
                new AccountUsageObservation(
                    secondObservationAt,
                    [new AllowanceWindow(25, TimeSpan.FromHours(5), secondObservationAt.AddHours(3))],
                    "team",
                    "Codex",
                    new AccountActivityObservation.NotRequested()),
                Local: null));

        Assert.Equal(secondObservationAt, snapshots.AllowanceObservedAt);
        Assert.Equal(firstObservationAt, snapshots.ActivityObservedAt);
        Assert.Equal(75, snapshots.FiveHour?.RemainingPercent);
        Assert.Equal("team", snapshots.Plan);
        Assert.Equal(123_456_789, snapshots.LifetimeTokens);
        Assert.Equal(987_654, snapshots.TodayTokens);
        Assert.False(snapshots.TodayTokensAreLocal);
        Assert.False(snapshots.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void RoutineRefreshClearsRetainedTodayAcrossLocalDates()
    {
        var firstObservationAt = new DateTimeOffset(
            new DateTime(2026, 9, 7, 23, 59, 0, DateTimeKind.Local));
        var snapshot = UsageSnapshotFixture.Create(
            new UsageObservations(
                new AccountUsageObservation(
                    firstObservationAt,
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
                    firstObservationAt.AddMinutes(2),
                    [],
                    "plus",
                    "Codex",
                    new AccountActivityObservation.NotRequested()),
                Local: null));

        Assert.Equal(12_000, snapshot.LifetimeTokens);
        Assert.True(snapshot.LifetimeIncludesLocalActivity);
        Assert.Null(snapshot.TodayTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.Equal(firstObservationAt, snapshot.ActivityObservedAt);
    }

    [Fact]
    public void ActivityRefreshUsesLocalTodayAndCompletesYesterdayLifetime()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: 10_000,
                TodayTokens: null,
                LatestDailyBucketDate: new DateOnly(2026, 9, 6)));
        var local = new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000);

        var snapshot = UsageSnapshotFixture.Create(account, local);

        Assert.Equal(2_000, snapshot.TodayTokens);
        Assert.True(snapshot.TodayTokensAreLocal);
        Assert.Equal(12_000, snapshot.LifetimeTokens);
        Assert.True(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ActivityRefreshPrefersAccountTodayOverLocalToday()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: 10_000,
                TodayTokens: 500,
                LatestDailyBucketDate: new DateOnly(2026, 9, 7)));
        var local = new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000);

        var snapshot = UsageSnapshotFixture.Create(account, local);

        Assert.Equal(500, snapshot.TodayTokens);
        Assert.Equal(10_000, snapshot.LifetimeTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ActivityRefreshIgnoresLocalActivityFromAnotherDate()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: 10_000,
                TodayTokens: null,
                LatestDailyBucketDate: new DateOnly(2026, 9, 6)));
        var local = new LocalUsageObservation(new DateOnly(2026, 9, 6), 2_000);

        var snapshot = UsageSnapshotFixture.Create(account, local);

        Assert.Null(snapshot.TodayTokens);
        Assert.Equal(10_000, snapshot.LifetimeTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ActivityRefreshDoesNotCompleteLifetimeWhenAccountHistoryIncludesToday()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: 10_000,
                TodayTokens: null,
                LatestDailyBucketDate: new DateOnly(2026, 9, 7)));
        var local = new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000);

        var snapshot = UsageSnapshotFixture.Create(account, local);

        Assert.Equal(2_000, snapshot.TodayTokens);
        Assert.Equal(10_000, snapshot.LifetimeTokens);
        Assert.True(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ActivityRefreshKeepsAccountLifetimeWhenLocalCompletionOverflows()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: long.MaxValue,
                TodayTokens: null,
                LatestDailyBucketDate: new DateOnly(2026, 9, 6)));
        var local = new LocalUsageObservation(new DateOnly(2026, 9, 7), 1);

        var snapshot = UsageSnapshotFixture.Create(account, local);

        Assert.Equal(1, snapshot.TodayTokens);
        Assert.Equal(long.MaxValue, snapshot.LifetimeTokens);
        Assert.True(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ActivityRefreshClearsPriorActivityWhenObservedValuesAreUnavailable()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = UsageSnapshotFixture.Create(
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

        Assert.Null(snapshot.LifetimeTokens);
        Assert.Null(snapshot.TodayTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ActivityRefreshUsesShortestAndLongestAllowanceWindowsAsFallbacks()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var shortest = new AllowanceWindow(10, TimeSpan.FromHours(1), observedAt.AddHours(1));
        var longest = new AllowanceWindow(20, TimeSpan.FromDays(1), observedAt.AddDays(1));
        var account = new AccountUsageObservation(
            observedAt,
            [longest, shortest],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: null,
                TodayTokens: null,
                LatestDailyBucketDate: null));

        var snapshot = UsageSnapshotFixture.Create(account);

        Assert.Equal(shortest, snapshot.FiveHour);
        Assert.Equal(longest, snapshot.Weekly);
    }

    [Fact]
    public void RoutineRefreshStartsWithoutActivity()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [new AllowanceWindow(24, TimeSpan.FromHours(5), observedAt.AddHours(3))],
            "plus",
            "Codex",
            new AccountActivityObservation.NotRequested());

        var snapshot = UsageSnapshotFixture.Create(
            account,
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000));

        Assert.Null(snapshot.LifetimeTokens);
        Assert.Null(snapshot.TodayTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }
}
