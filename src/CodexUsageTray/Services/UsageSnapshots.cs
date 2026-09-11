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

internal sealed class UsageSnapshots : IAsyncDisposable, IUsageSnapshotRefresher
{
    private static readonly TimeSpan MinimumFiveHourDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan MaximumFiveHourDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinimumWeeklyDuration = TimeSpan.FromMinutes(9_000);
    private static readonly TimeSpan MaximumWeeklyDuration = TimeSpan.FromMinutes(11_000);
    private readonly object sync = new();
    private readonly IUsageObservationReader observations;
    private readonly CancellationTokenSource lifetime = new();
    private UsageSnapshot? current;
    private RefreshWave? activeWave;
    private bool disposed;

    internal UsageSnapshots(IUsageObservationReader observations)
    {
        this.observations = observations;
    }

    public UsageSnapshot? Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(UsageObservationRequest.AllowanceWindows, cancellationToken);

    public Task<UsageSnapshot> RefreshWithActivityAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(UsageObservationRequest.AllowanceWindowsAndActivity, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Task? running;
        RefreshWave? wave;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            wave = activeWave;
            running = wave?.Runner;
            wave?.Completion.TrySetCanceled(new CancellationToken(canceled: true));
        }

        lifetime.Cancel();
        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposal owns cancellation of the shared adapter read.
            }
        }

        lifetime.Dispose();
    }

    private Task<UsageSnapshot> RequestAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<UsageSnapshot> shared;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (activeWave is null)
            {
                activeWave = new RefreshWave(request, current);
                var wave = activeWave;
                wave.Runner = Task.Run(() => RunWaveAsync(wave), CancellationToken.None);
            }
            else if (request == UsageObservationRequest.AllowanceWindowsAndActivity)
            {
                activeWave.ActivityRequested = true;
            }

            shared = activeWave.Completion.Task;
        }

        return cancellationToken.CanBeCanceled
            ? shared.WaitAsync(cancellationToken)
            : shared;
    }

    private async Task RunWaveAsync(RefreshWave wave)
    {
        var request = wave.InitialRequest;
        var candidate = wave.Previous;

        while (true)
        {
            try
            {
                var observed = await observations.ReadAsync(request, lifetime.Token).ConfigureAwait(false);
                candidate = Reconcile(candidate, observed.Account, observed.Local);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                CompleteCanceled(wave);
                return;
            }
            catch (Exception exception)
            {
                if (ContinueWithActivityOrCompleteFailed(wave, request, exception))
                {
                    request = UsageObservationRequest.AllowanceWindowsAndActivity;
                    continue;
                }

                return;
            }

            lock (sync)
            {
                if (disposed)
                {
                    activeWave = null;
                    wave.Completion.TrySetCanceled(new CancellationToken(canceled: true));
                    return;
                }

                if (request == UsageObservationRequest.AllowanceWindows && wave.ActivityRequested)
                {
                    request = UsageObservationRequest.AllowanceWindowsAndActivity;
                    continue;
                }

                current = candidate;
                activeWave = null;
                wave.Completion.TrySetResult(candidate);
                return;
            }
        }
    }

    private bool ContinueWithActivityOrCompleteFailed(
        RefreshWave wave,
        UsageObservationRequest request,
        Exception exception)
    {
        var failure = exception as UsageSnapshotRefreshException
            ?? new UsageSnapshotRefreshException(exception.Message, exception);

        lock (sync)
        {
            if (!disposed
                && request == UsageObservationRequest.AllowanceWindows
                && wave.ActivityRequested)
            {
                return true;
            }

            if (ReferenceEquals(activeWave, wave))
            {
                activeWave = null;
            }

            if (disposed)
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

    private void CompleteCanceled(RefreshWave wave)
    {
        lock (sync)
        {
            if (ReferenceEquals(activeWave, wave))
            {
                activeWave = null;
            }

            wave.Completion.TrySetCanceled(lifetime.Token);
        }
    }

    private static UsageSnapshot Reconcile(
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
