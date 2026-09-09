namespace CodexUsageTray;

internal sealed record UsageSnapshot
{
    private static readonly TimeSpan MinimumFiveHourDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan MaximumFiveHourDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinimumWeeklyDuration = TimeSpan.FromMinutes(9_000);
    private static readonly TimeSpan MaximumWeeklyDuration = TimeSpan.FromMinutes(11_000);

    private UsageSnapshot(
        DateTimeOffset retrievedAt,
        AllowanceWindow? fiveHour,
        AllowanceWindow? weekly,
        long? lifetimeTokens,
        long? todayTokens,
        string? plan,
        string? limitName,
        bool todayTokensAreLocal = false,
        bool lifetimeIncludesLocalToday = false)
    {
        RetrievedAt = retrievedAt;
        FiveHour = fiveHour;
        Weekly = weekly;
        LifetimeTokens = lifetimeTokens;
        TodayTokens = todayTokens;
        Plan = plan;
        LimitName = limitName;
        TodayTokensAreLocal = todayTokensAreLocal;
        LifetimeIncludesLocalToday = lifetimeIncludesLocalToday;
    }

    public DateTimeOffset RetrievedAt { get; }
    public AllowanceWindow? FiveHour { get; }
    public AllowanceWindow? Weekly { get; }
    public long? LifetimeTokens { get; }
    public long? TodayTokens { get; }
    public string? Plan { get; }
    public string? LimitName { get; }
    public bool TodayTokensAreLocal { get; }
    public bool LifetimeIncludesLocalToday { get; }

    public static UsageSnapshot Reconcile(
        UsageSnapshot? previous,
        AccountUsageObservation account,
        LocalUsageObservation? local)
    {
        var fiveHour = account.AllowanceWindows
            .FirstOrDefault(window => window.Duration is { } duration
                && duration >= MinimumFiveHourDuration
                && duration <= MaximumFiveHourDuration)
            ?? account.AllowanceWindows.OrderBy(window => window.Duration ?? TimeSpan.MaxValue).FirstOrDefault();
        var weekly = account.AllowanceWindows
            .FirstOrDefault(window => window.Duration is { } duration
                && duration >= MinimumWeeklyDuration
                && duration <= MaximumWeeklyDuration)
            ?? account.AllowanceWindows
                .OrderByDescending(window => window.Duration ?? TimeSpan.MinValue)
                .FirstOrDefault(window => window != fiveHour);

        if (account.Activity is AccountActivityObservation.NotRequested)
        {
            return new UsageSnapshot(
                account.ObservedAt,
                fiveHour,
                weekly,
                previous?.LifetimeTokens,
                previous?.TodayTokens,
                account.Plan,
                account.LimitName,
                previous?.TodayTokensAreLocal ?? false,
                previous?.LifetimeIncludesLocalToday ?? false);
        }

        var activity = (AccountActivityObservation.Observed)account.Activity;
        var observedDate = DateOnly.FromDateTime(account.ObservedAt.LocalDateTime);
        var useLocalToday = activity.TodayTokens is null && local?.Date == observedDate;
        var todayTokens = useLocalToday ? local!.TodayTokens : activity.TodayTokens;
        var lifetimeTokens = activity.LifetimeTokens;
        var lifetimeIncludesLocalToday = false;
        if (useLocalToday
            && lifetimeTokens is { } accountLifetime
            && activity.LatestDailyBucketDate == observedDate.AddDays(-1))
        {
            try
            {
                lifetimeTokens = checked(accountLifetime + local!.TodayTokens);
                lifetimeIncludesLocalToday = true;
            }
            catch (OverflowException)
            {
                // Keep the account lifetime when the combined value cannot be represented.
            }
        }

        return new UsageSnapshot(
            account.ObservedAt,
            fiveHour,
            weekly,
            lifetimeTokens,
            todayTokens,
            account.Plan,
            account.LimitName,
            useLocalToday,
            lifetimeIncludesLocalToday);
    }
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
