namespace CodexUsageTray.Tests;

public sealed class UsageSnapshotsTests
{
    [Fact]
    public async Task RefreshPublishesUsageSnapshot()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedUsageObservationReader();
        var read = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        await using var snapshots = new UsageSnapshots(observations);

        var refresh = snapshots.RefreshAsync();
        await read.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        read.Succeed(Observe(observedAt, usedPercent: 20, includeActivity: false));

        var snapshot = await refresh;

        Assert.Same(snapshot, snapshots.Current);
        Assert.Equal(80, snapshot.FiveHour?.RemainingPercent);
        Assert.Single(observations.Requests);
    }

    [Fact]
    public async Task ActivityRequestEscalatesSharedRoutineWaveOnce()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedUsageObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        await using var snapshots = new UsageSnapshots(observations);

        var routineRefresh = snapshots.RefreshAsync();
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstActivityRefresh = snapshots.RefreshWithActivityAsync();
        var secondActivityRefresh = snapshots.RefreshWithActivityAsync();
        routineRead.Succeed(Observe(observedAt, usedPercent: 20, includeActivity: false));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Succeed(Observe(observedAt.AddSeconds(1), usedPercent: 21, includeActivity: true));

        var results = await Task.WhenAll(routineRefresh, firstActivityRefresh, secondActivityRefresh);

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Same(results[0], snapshots.Current);
        Assert.Equal(1234, results[0].TodayTokens);
        Assert.Equal(
            [UsageObservationRequest.AllowanceWindows, UsageObservationRequest.AllowanceWindowsAndActivity],
            observations.Requests);
        Assert.Equal(1, observations.MaximumConcurrentReads);
    }

    [Fact]
    public async Task ActivityReadStillRunsWhenRoutineReadFails()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedUsageObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        await using var snapshots = new UsageSnapshots(observations);

        var routineRefresh = snapshots.RefreshAsync();
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activityRefresh = snapshots.RefreshWithActivityAsync();
        routineRead.Fail(new IOException("routine read failed"));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Succeed(Observe(observedAt, usedPercent: 21, includeActivity: true));

        var results = await Task.WhenAll(routineRefresh, activityRefresh);

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Same(results[0], snapshots.Current);
    }

    [Fact]
    public async Task FailedEscalatedWaveDoesNotPublishRoutineResult()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedUsageObservationReader();
        var initialRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        await using var snapshots = new UsageSnapshots(observations);

        var initialRefresh = snapshots.RefreshWithActivityAsync();
        await initialRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        initialRead.Succeed(Observe(observedAt, usedPercent: 10, includeActivity: true));
        var initial = await initialRefresh;

        var routineRefresh = snapshots.RefreshAsync();
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activityRefresh = snapshots.RefreshWithActivityAsync();
        routineRead.Succeed(Observe(observedAt.AddMinutes(1), usedPercent: 20, includeActivity: false));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Fail(new IOException("activity read failed"));

        var routineFailure = await Assert.ThrowsAsync<UsageSnapshotRefreshException>(() => routineRefresh);
        var activityFailure = await Assert.ThrowsAsync<UsageSnapshotRefreshException>(() => activityRefresh);

        Assert.Equal("activity read failed", routineFailure.Message);
        Assert.Equal("activity read failed", activityFailure.Message);
        Assert.Same(initial, snapshots.Current);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedWaveOrRetractEscalation()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedUsageObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        await using var snapshots = new UsageSnapshots(observations);
        using var callerCancellation = new CancellationTokenSource();

        var routineRefresh = snapshots.RefreshAsync();
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activityRefresh = snapshots.RefreshWithActivityAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activityRefresh);
        routineRead.Succeed(Observe(observedAt, usedPercent: 20, includeActivity: false));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Succeed(Observe(observedAt.AddSeconds(1), usedPercent: 21, includeActivity: true));

        var snapshot = await routineRefresh;

        Assert.Equal(1234, snapshot.TodayTokens);
        Assert.False(routineRead.AdapterCancellation.IsCancellationRequested);
        Assert.False(activityRead.AdapterCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposalCancelsAdapterAndWaitingCallers()
    {
        var observations = new ScriptedUsageObservationReader();
        var read = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var snapshots = new UsageSnapshots(observations);

        var refresh = snapshots.RefreshAsync();
        await read.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await snapshots.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(read.AdapterCancellation.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => snapshots.RefreshAsync());
    }

    [Fact]
    public async Task DisposalCancelsWaitersBeforeAdapterCleanupCompletes()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new CancellationIgnoringUsageObservationReader();
        var snapshots = new UsageSnapshots(observations);

        var refresh = snapshots.RefreshAsync();
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = snapshots.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observations.AdapterCancellation.IsCancellationRequested);
        Assert.False(disposal.IsCompleted);
        Assert.Null(snapshots.Current);

        observations.Completion.TrySetResult(Observe(observedAt, usedPercent: 20, includeActivity: false));
        await disposal;

        Assert.Null(snapshots.Current);
    }

    private static UsageObservations Observe(
        DateTimeOffset observedAt,
        int usedPercent,
        bool includeActivity)
    {
        var activity = includeActivity
            ? (AccountActivityObservation)new AccountActivityObservation.Observed(
                LifetimeTokens: 5678,
                TodayTokens: 1234,
                LatestDailyBucketDate: DateOnly.FromDateTime(observedAt.LocalDateTime))
            : new AccountActivityObservation.NotRequested();
        var account = new AccountUsageObservation(
            observedAt,
            [new AllowanceWindow(usedPercent, TimeSpan.FromHours(5), observedAt.AddHours(1))],
            "plus",
            "Codex",
            activity);
        return new UsageObservations(account, Local: null);
    }

    private sealed class ScriptedUsageObservationReader : IUsageObservationReader
    {
        private readonly object sync = new();
        private readonly Queue<ReadStep> steps = new();
        private readonly List<UsageObservationRequest> requests = [];
        private int activeReads;

        public IReadOnlyList<UsageObservationRequest> Requests
        {
            get
            {
                lock (sync)
                {
                    return requests.ToArray();
                }
            }
        }

        public int MaximumConcurrentReads { get; private set; }

        public ReadStep Enqueue(UsageObservationRequest expected)
        {
            var step = new ReadStep(expected);
            steps.Enqueue(step);
            return step;
        }

        public async Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            ReadStep step;
            lock (sync)
            {
                step = steps.Dequeue();
                Assert.Equal(step.Expected, request);
                requests.Add(request);
                activeReads++;
                MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, activeReads);
                step.AdapterCancellation = cancellationToken;
                step.Started.TrySetResult();
            }

            try
            {
                return await step.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (sync)
                {
                    activeReads--;
                }
            }
        }
    }

    private sealed class ReadStep(UsageObservationRequest expected)
    {
        public UsageObservationRequest Expected { get; } = expected;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UsageObservations> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken AdapterCancellation { get; set; }

        public void Succeed(UsageObservations observations) => Completion.TrySetResult(observations);

        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class CancellationIgnoringUsageObservationReader : IUsageObservationReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UsageObservations> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken AdapterCancellation { get; private set; }

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(UsageObservationRequest.AllowanceWindows, request);
            AdapterCancellation = cancellationToken;
            Started.TrySetResult();
            return Completion.Task;
        }
    }
}
