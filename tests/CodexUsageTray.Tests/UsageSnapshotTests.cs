namespace CodexUsageTray.Tests;

public sealed class UsageSnapshotTests
{
    [Fact]
    public void ReconcileCreatesSnapshotFromObservedAccountUsage()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local: null);

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
    public void ReconcileRetainsActivityWhenAccountActivityWasNotRequested()
    {
        var firstObservationAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var previous = UsageSnapshot.Reconcile(
            previous: null,
            new AccountUsageObservation(
                firstObservationAt,
                [new AllowanceWindow(24, TimeSpan.FromHours(5), firstObservationAt.AddHours(3))],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 123_456_789,
                    TodayTokens: 987_654,
                    LatestDailyBucketDate: DateOnly.FromDateTime(firstObservationAt.LocalDateTime))),
            local: null);
        var secondObservationAt = firstObservationAt.AddMinutes(1);
        var rateOnly = new AccountUsageObservation(
            secondObservationAt,
            [new AllowanceWindow(25, TimeSpan.FromHours(5), secondObservationAt.AddHours(3))],
            "team",
            "Codex",
            new AccountActivityObservation.NotRequested());

        var snapshot = UsageSnapshot.Reconcile(previous, rateOnly, local: null);

        Assert.Equal(secondObservationAt, snapshot.AllowanceObservedAt);
        Assert.Equal(firstObservationAt, snapshot.ActivityObservedAt);
        Assert.Equal(75, snapshot.FiveHour?.RemainingPercent);
        Assert.Equal("team", snapshot.Plan);
        Assert.Equal(previous.LifetimeTokens, snapshot.LifetimeTokens);
        Assert.Equal(previous.TodayTokens, snapshot.TodayTokens);
        Assert.Equal(previous.TodayTokensAreLocal, snapshot.TodayTokensAreLocal);
        Assert.Equal(previous.LifetimeIncludesLocalActivity, snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcileClearsRetainedTodayAcrossLocalDates()
    {
        var firstObservationAt = new DateTimeOffset(2026, 9, 7, 23, 59, 0, TimeSpan.FromHours(2));
        var previous = UsageSnapshot.Reconcile(
            previous: null,
            new AccountUsageObservation(
                firstObservationAt,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: null,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 6))),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000));
        var nextDateObservation = new AccountUsageObservation(
            firstObservationAt.AddMinutes(2),
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.NotRequested());

        var snapshot = UsageSnapshot.Reconcile(previous, nextDateObservation, local: null);

        Assert.Equal(12_000, snapshot.LifetimeTokens);
        Assert.True(snapshot.LifetimeIncludesLocalActivity);
        Assert.Null(snapshot.TodayTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.Equal(firstObservationAt, snapshot.ActivityObservedAt);
    }

    [Fact]
    public void ReconcileUsesLocalTodayAndCompletesYesterdayLifetime()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local);

        Assert.Equal(2_000, snapshot.TodayTokens);
        Assert.True(snapshot.TodayTokensAreLocal);
        Assert.Equal(12_000, snapshot.LifetimeTokens);
        Assert.True(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcilePrefersAccountTodayOverLocalToday()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local);

        Assert.Equal(500, snapshot.TodayTokens);
        Assert.Equal(10_000, snapshot.LifetimeTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcileIgnoresLocalActivityFromAnotherDate()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local);

        Assert.Null(snapshot.TodayTokens);
        Assert.Equal(10_000, snapshot.LifetimeTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcileDoesNotCompleteLifetimeWhenAccountHistoryIncludesToday()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local);

        Assert.Equal(2_000, snapshot.TodayTokens);
        Assert.Equal(10_000, snapshot.LifetimeTokens);
        Assert.True(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcileKeepsAccountLifetimeWhenLocalCompletionOverflows()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local);

        Assert.Equal(1, snapshot.TodayTokens);
        Assert.Equal(long.MaxValue, snapshot.LifetimeTokens);
        Assert.True(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcileClearsPriorActivityWhenObservedValuesAreUnavailable()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var previous = UsageSnapshot.Reconcile(
            previous: null,
            new AccountUsageObservation(
                observedAt.AddMinutes(-1),
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: 2_000,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 7))),
            local: null);
        var unavailable = new AccountUsageObservation(
            observedAt,
            [],
            "plus",
            "Codex",
            new AccountActivityObservation.Observed(
                LifetimeTokens: null,
                TodayTokens: null,
                LatestDailyBucketDate: null));

        var snapshot = UsageSnapshot.Reconcile(previous, unavailable, local: null);

        Assert.Null(snapshot.LifetimeTokens);
        Assert.Null(snapshot.TodayTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }

    [Fact]
    public void ReconcileUsesShortestAndLongestAllowanceWindowsAsFallbacks()
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

        var snapshot = UsageSnapshot.Reconcile(previous: null, account, local: null);

        Assert.Equal(shortest, snapshot.FiveHour);
        Assert.Equal(longest, snapshot.Weekly);
    }

    [Fact]
    public void ReconcileStartsWithoutActivityWhenOnlyAllowancesWereObserved()
    {
        var observedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var account = new AccountUsageObservation(
            observedAt,
            [new AllowanceWindow(24, TimeSpan.FromHours(5), observedAt.AddHours(3))],
            "plus",
            "Codex",
            new AccountActivityObservation.NotRequested());

        var snapshot = UsageSnapshot.Reconcile(
            previous: null,
            account,
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000));

        Assert.Null(snapshot.LifetimeTokens);
        Assert.Null(snapshot.TodayTokens);
        Assert.False(snapshot.TodayTokensAreLocal);
        Assert.False(snapshot.LifetimeIncludesLocalActivity);
    }
}
