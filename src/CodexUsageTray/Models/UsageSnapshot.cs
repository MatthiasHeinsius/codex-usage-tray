namespace CodexUsageTray;

internal sealed record UsageSnapshot(
    DateTimeOffset RetrievedAt,
    UsageWindow? FiveHour,
    UsageWindow? Weekly,
    long? LifetimeTokens,
    long? TodayTokens,
    string? Plan,
    string? LimitName,
    bool TodayTokensAreLocal = false,
    bool LifetimeIncludesLocalToday = false);

internal sealed record UsageWindow(
    int UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
