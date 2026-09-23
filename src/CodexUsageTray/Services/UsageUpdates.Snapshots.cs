namespace CodexUsageTray;

internal enum UsageObservationRequest
{
    AllowanceWindows,
    AllowanceWindowsAndActivity
}

internal interface IUsageObservationReader
{
    Task<UsageObservations> ReadAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken);
}

internal sealed partial class UsageUpdates
{
    private static readonly TimeSpan MinimumFiveHourDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan MaximumFiveHourDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinimumWeeklyDuration = TimeSpan.FromMinutes(9_000);
    private static readonly TimeSpan MaximumWeeklyDuration = TimeSpan.FromMinutes(11_000);
    // The Usage Update gate owns snapshot reads and reconciliation.
    private UsageSnapshot? currentSnapshot;

    private async Task<UsageSnapshot> RequestSnapshotAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Process startup and local history reads must not block the UI thread.
        var observed = await Task.Run(
            () => observations.ReadAsync(request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        currentSnapshot = ReconcileSnapshot(currentSnapshot, observed.Account, observed.Local);
        return currentSnapshot;
    }

    private static UsageSnapshot ReconcileSnapshot(
        UsageSnapshot? previous,
        AccountUsageObservation account,
        LocalUsageObservation? local)
    {
        var fiveHour = account.AllowanceWindows
            .FirstOrDefault(window => window.Duration is { } duration
                && duration >= MinimumFiveHourDuration
                && duration <= MaximumFiveHourDuration);
        var weekly = account.AllowanceWindows
            .FirstOrDefault(window => window.Duration is { } duration
                && duration >= MinimumWeeklyDuration
                && duration <= MaximumWeeklyDuration);
        fiveHour ??= account.AllowanceWindows
            .OrderBy(window => window.Duration ?? TimeSpan.MaxValue)
            .FirstOrDefault(window => window != weekly);
        weekly ??= account.AllowanceWindows
                .OrderByDescending(window => window.Duration ?? TimeSpan.MinValue)
                .FirstOrDefault(window => window != fiveHour);

        if (account.Activity is AccountActivityObservation.NotRequested)
        {
            var sameAccount = !string.IsNullOrWhiteSpace(account.AccountEmail)
                && string.Equals(account.AccountEmail, previous?.AccountEmail, StringComparison.OrdinalIgnoreCase);
            var retainsToday = sameAccount
                && previous?.ActivityObservedAt is { } activityObservedAt
                && DateOnly.FromDateTime(activityObservedAt.LocalDateTime)
                    == DateOnly.FromDateTime(account.ObservedAt.LocalDateTime);
            return new UsageSnapshot(
                account.ObservedAt,
                sameAccount ? previous?.ActivityObservedAt : null,
                fiveHour,
                weekly,
                sameAccount ? previous?.LifetimeTokens : null,
                retainsToday ? previous?.TodayTokens : null,
                account.Plan,
                account.LimitName,
                account.AccountEmail);
        }

        var activity = (AccountActivityObservation.Observed)account.Activity;
        var observedDate = DateOnly.FromDateTime(account.ObservedAt.LocalDateTime);
        var useLocalToday = activity.TodayTokens is null && local?.Date == observedDate;
        var todayTokens = useLocalToday ? local!.TodayTokens : activity.TodayTokens;
        var lifetimeTokens = activity.LifetimeTokens;
        if (useLocalToday
            && lifetimeTokens is { } accountLifetime
            && activity.LatestDailyBucketDate == observedDate.AddDays(-1))
        {
            try
            {
                lifetimeTokens = checked(accountLifetime + local!.TodayTokens);
            }
            catch (OverflowException)
            {
                // Keep the account lifetime when the combined value cannot be represented.
            }
        }

        return new UsageSnapshot(
            account.ObservedAt,
            account.ObservedAt,
            fiveHour,
            weekly,
            lifetimeTokens,
            todayTokens,
            account.Plan,
            account.LimitName,
            account.AccountEmail);
    }
}
