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
    private readonly object snapshotSync = new();
    private UsageSnapshot? currentSnapshot;
    private RefreshWave? activeRefreshWave;

    private Task<UsageSnapshot> RefreshSnapshotAsync(
        bool includeActivity,
        CancellationToken cancellationToken) =>
        RequestSnapshotAsync(
            includeActivity
                ? UsageObservationRequest.AllowanceWindowsAndActivity
                : UsageObservationRequest.AllowanceWindows,
            cancellationToken);

    private Task<UsageSnapshot> RequestSnapshotAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<UsageSnapshot> shared;
        lock (snapshotSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

            if (activeRefreshWave is null)
            {
                activeRefreshWave = new RefreshWave(request, currentSnapshot);
                var wave = activeRefreshWave;
                wave.Runner = Task.Run(() => RunRefreshWaveAsync(wave), CancellationToken.None);
            }
            else if (request == UsageObservationRequest.AllowanceWindowsAndActivity)
            {
                activeRefreshWave.ActivityRequested = true;
            }

            shared = activeRefreshWave.Completion.Task;
        }

        return cancellationToken.CanBeCanceled
            ? shared.WaitAsync(cancellationToken)
            : shared;
    }

    private async Task RunRefreshWaveAsync(RefreshWave wave)
    {
        var request = wave.InitialRequest;
        var candidate = wave.Previous;

        while (true)
        {
            try
            {
                var observed = await observations.ReadAsync(request, lifetime.Token).ConfigureAwait(false);
                candidate = ReconcileSnapshot(candidate, observed.Account, observed.Local);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                CompleteCanceledRefresh(wave);
                return;
            }
            catch (Exception exception)
            {
                if (ContinueWithActivityOrCompleteFailedRefresh(wave, request, exception))
                {
                    request = UsageObservationRequest.AllowanceWindowsAndActivity;
                    continue;
                }

                return;
            }

            lock (snapshotSync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    activeRefreshWave = null;
                    wave.Completion.TrySetCanceled(new CancellationToken(canceled: true));
                    return;
                }

                if (request == UsageObservationRequest.AllowanceWindows && wave.ActivityRequested)
                {
                    request = UsageObservationRequest.AllowanceWindowsAndActivity;
                    continue;
                }

                currentSnapshot = candidate;
                activeRefreshWave = null;
                wave.Completion.TrySetResult(candidate);
                return;
            }
        }
    }

    private bool ContinueWithActivityOrCompleteFailedRefresh(
        RefreshWave wave,
        UsageObservationRequest request,
        Exception exception)
    {
        var failure = exception as UsageSnapshotRefreshException
            ?? new UsageSnapshotRefreshException(exception.Message, exception);

        lock (snapshotSync)
        {
            if (Volatile.Read(ref disposed) == 0
                && request == UsageObservationRequest.AllowanceWindows
                && wave.ActivityRequested)
            {
                return true;
            }

            if (ReferenceEquals(activeRefreshWave, wave))
            {
                activeRefreshWave = null;
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                wave.Completion.TrySetCanceled(new CancellationToken(canceled: true));
            }
            else
            {
                wave.Completion.TrySetException(failure);
            }

            return false;
        }
    }

    private void CompleteCanceledRefresh(RefreshWave wave)
    {
        lock (snapshotSync)
        {
            if (ReferenceEquals(activeRefreshWave, wave))
            {
                activeRefreshWave = null;
            }

            wave.Completion.TrySetCanceled(lifetime.Token);
        }
    }

    private Task? CancelActiveRefresh()
    {
        lock (snapshotSync)
        {
            var wave = activeRefreshWave;
            wave?.Completion.TrySetCanceled(new CancellationToken(canceled: true));
            return wave?.Runner;
        }
    }

    private static UsageSnapshot ReconcileSnapshot(
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
            var retainsToday = previous?.ActivityObservedAt is { } activityObservedAt
                && DateOnly.FromDateTime(activityObservedAt.LocalDateTime)
                    == DateOnly.FromDateTime(account.ObservedAt.LocalDateTime);
            return new UsageSnapshot(
                account.ObservedAt,
                previous?.ActivityObservedAt,
                fiveHour,
                weekly,
                previous?.LifetimeTokens,
                retainsToday ? previous?.TodayTokens : null,
                account.Plan,
                account.LimitName,
                retainsToday && (previous?.TodayTokensAreLocal ?? false),
                previous?.LifetimeIncludesLocalActivity ?? false);
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

    private sealed class RefreshWave(
        UsageObservationRequest initialRequest,
        UsageSnapshot? previous)
    {
        public UsageObservationRequest InitialRequest { get; } = initialRequest;
        public UsageSnapshot? Previous { get; } = previous;
        public bool ActivityRequested { get; set; } =
            initialRequest == UsageObservationRequest.AllowanceWindowsAndActivity;
        public TaskCompletionSource<UsageSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Runner { get; set; }
    }
}

internal sealed class UsageSnapshotRefreshException : Exception
{
    public UsageSnapshotRefreshException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
