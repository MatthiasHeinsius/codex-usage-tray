namespace CodexUsageTray;

internal sealed record UsageSnapshot
{
    internal UsageSnapshot(
        DateTimeOffset allowanceObservedAt,
        DateTimeOffset? activityObservedAt,
        AllowanceWindow? fiveHour,
        AllowanceWindow? weekly,
        long? lifetimeTokens,
        long? todayTokens,
        string? plan,
        string? limitName,
        bool todayTokensAreLocal = false,
        bool lifetimeIncludesLocalActivity = false)
    {
        AllowanceObservedAt = allowanceObservedAt;
        ActivityObservedAt = activityObservedAt;
        FiveHour = fiveHour;
        Weekly = weekly;
        LifetimeTokens = lifetimeTokens;
        TodayTokens = todayTokens;
        Plan = plan;
        LimitName = limitName;
        TodayTokensAreLocal = todayTokensAreLocal;
        LifetimeIncludesLocalActivity = lifetimeIncludesLocalActivity;
    }

    public DateTimeOffset AllowanceObservedAt { get; }
    public DateTimeOffset? ActivityObservedAt { get; }
    public AllowanceWindow? FiveHour { get; }
    public AllowanceWindow? Weekly { get; }
    public long? LifetimeTokens { get; }
    public long? TodayTokens { get; }
    public string? Plan { get; }
    public string? LimitName { get; }
    public bool TodayTokensAreLocal { get; }
    public bool LifetimeIncludesLocalActivity { get; }

}

internal sealed record AllowanceWindow(
    int UsedPercent,
    TimeSpan? Duration,
    DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record AccountUsageObservation(
    DateTimeOffset ObservedAt,
    IReadOnlyList<AllowanceWindow> AllowanceWindows,
    string? Plan,
    string? LimitName,
    AccountActivityObservation Activity);

internal abstract record AccountActivityObservation
{
    private AccountActivityObservation()
    {
    }

    internal sealed record NotRequested : AccountActivityObservation;

    internal sealed record Observed(
        long? LifetimeTokens,
        long? TodayTokens,
        DateOnly? LatestDailyBucketDate) : AccountActivityObservation;
}

internal sealed record LocalUsageObservation(DateOnly Date, long TodayTokens);

internal sealed record UsageObservations(
    AccountUsageObservation Account,
    LocalUsageObservation? Local);
