using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public async Task CallerCancellationDoesNotCancelActiveObservation()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new SharedObservationReader();
        using var callerCancellation = new CancellationTokenSource();
        await using var updates = CreateRefreshUpdates(observations);

        var canceledRefresh = updates.RefreshAsync(callerCancellation.Token);
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRefresh);

        var survivingRefresh = updates.RefreshAsync();
        observations.Completion.TrySetResult(ObserveRefresh(observedAt, usedPercent: 20));
        var presentation = await survivingRefresh;

        Assert.Equal("80% left", presentation.Popup.FiveHour.RemainingText);
        Assert.Equal(1, observations.CallCount);
        Assert.False(observations.AdapterCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ActivityUpdateEscalatesActiveRoutineObservationOnce()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        using var routineCancellation = new CancellationTokenSource();
        await using var updates = CreateRefreshUpdates(observations);

        var canceledRoutine = updates.RefreshAsync(routineCancellation.Token);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        routineCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRoutine);

        var activityUpdate = updates.RefreshWithActivityAsync();
        routineRead.Succeed(ObserveRefresh(observedAt, usedPercent: 20));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Succeed(ObserveRefresh(observedAt.AddSeconds(1), usedPercent: 21, includeActivity: true));

        var presentation = await activityUpdate;

        Assert.Equal("1.2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal(
            [UsageObservationRequest.AllowanceWindows, UsageObservationRequest.AllowanceWindowsAndActivity],
            observations.Requests);
        Assert.Equal(1, observations.MaximumConcurrentReads);
    }

    [Fact]
    public async Task ActivityObservationStillRunsWhenRoutineObservationFails()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        using var routineCancellation = new CancellationTokenSource();
        await using var updates = CreateRefreshUpdates(observations);

        var canceledRoutine = updates.RefreshAsync(routineCancellation.Token);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        routineCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRoutine);

        var activityUpdate = updates.RefreshWithActivityAsync();
        routineRead.Fail(new IOException("routine observation failed"));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Succeed(ObserveRefresh(observedAt, usedPercent: 21, includeActivity: true));

        var presentation = await activityUpdate;

        Assert.Equal("1.2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal(2, observations.Requests.Length);
    }

    [Fact]
    public async Task CanceledActivityUpdateDoesNotRetractEscalation()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        using var routineCancellation = new CancellationTokenSource();
        using var activityCancellation = new CancellationTokenSource();
        await using var updates = CreateRefreshUpdates(observations);

        var canceledRoutine = updates.RefreshAsync(routineCancellation.Token);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        routineCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRoutine);

        var canceledActivity = updates.RefreshWithActivityAsync(activityCancellation.Token);
        activityCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledActivity);
        var survivingUpdate = updates.RefreshAsync();

        routineRead.Succeed(ObserveRefresh(observedAt, usedPercent: 20));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Succeed(ObserveRefresh(observedAt.AddSeconds(1), usedPercent: 21, includeActivity: true));

        var presentation = await survivingUpdate;

        Assert.Equal("1.2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal(2, observations.Requests.Length);
    }

    [Fact]
    public async Task FailedEscalatedObservationLetsNextUpdateStartFreshWave()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        var nextRoutineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        using var routineCancellation = new CancellationTokenSource();
        await using var updates = CreateRefreshUpdates(observations);

        var canceledRoutine = updates.RefreshAsync(routineCancellation.Token);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        routineCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRoutine);

        var failedActivity = updates.RefreshWithActivityAsync();
        routineRead.Succeed(ObserveRefresh(observedAt, usedPercent: 20));
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activityRead.Fail(new IOException("activity observation failed"));

        var failure = await Assert.ThrowsAsync<UsageSnapshotRefreshException>(() => failedActivity);
        Assert.Equal("activity observation failed", failure.Message);

        var nextUpdate = updates.RefreshAsync();
        await nextRoutineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        nextRoutineRead.Succeed(ObserveRefresh(observedAt.AddMinutes(1), usedPercent: 30));

        var presentation = await nextUpdate;
        Assert.Equal("70% left", presentation.Popup.FiveHour.RemainingText);
    }

    [Fact]
    public async Task DisposalCancelsCallersBeforeAdapterCleanupCompletes()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new CancellationIgnoringObservationReader();
        var updates = CreateRefreshUpdates(observations);

        var refresh = updates.RefreshAsync();
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = updates.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observations.AdapterCancellation.IsCancellationRequested);
        Assert.False(disposal.IsCompleted);

        observations.Completion.TrySetResult(ObserveRefresh(observedAt, usedPercent: 20));
        await disposal;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => updates.RefreshAsync());
    }

    private static UsageUpdates CreateRefreshUpdates(IUsageObservationReader observations) =>
        new(
            observations,
            new NoOpActivationCommand(),
            new DisabledActivationSettings(),
            TimeProvider.System,
            CultureInfo.InvariantCulture);

    private static UsageObservations ObserveRefresh(
        DateTimeOffset observedAt,
        int usedPercent,
        bool includeActivity = false)
    {
        var activity = includeActivity
            ? (AccountActivityObservation)new AccountActivityObservation.Observed(
                LifetimeTokens: 5678,
                TodayTokens: 1234,
                LatestDailyBucketDate: DateOnly.FromDateTime(observedAt.LocalDateTime))
            : new AccountActivityObservation.NotRequested();
        return new UsageObservations(
            new AccountUsageObservation(
                observedAt,
                [new AllowanceWindow(usedPercent, TimeSpan.FromHours(5), observedAt.AddHours(1))],
                "plus",
                "Codex",
                activity),
            Local: null);
    }

    private sealed class SharedObservationReader : IUsageObservationReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UsageObservations> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public CancellationToken AdapterCancellation { get; private set; }

        public async Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(UsageObservationRequest.AllowanceWindows, request);
            CallCount++;
            AdapterCancellation = cancellationToken;
            Started.TrySetResult();
            return await Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ScriptedObservationReader : IUsageObservationReader
    {
        private readonly object sync = new();
        private readonly Queue<ObservationReadStep> steps = new();
        private readonly List<UsageObservationRequest> requests = [];
        private int activeReads;

        public UsageObservationRequest[] Requests
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

        public ObservationReadStep Enqueue(UsageObservationRequest expected)
        {
            var step = new ObservationReadStep(expected);
            steps.Enqueue(step);
            return step;
        }

        public async Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            ObservationReadStep step;
            lock (sync)
            {
                step = steps.Dequeue();
                Assert.Equal(step.Expected, request);
                requests.Add(request);
                activeReads++;
                MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, activeReads);
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

    private sealed class ObservationReadStep(UsageObservationRequest expected)
    {
        public UsageObservationRequest Expected { get; } = expected;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UsageObservations> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Succeed(UsageObservations observations) => Completion.TrySetResult(observations);

        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class CancellationIgnoringObservationReader : IUsageObservationReader
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

    private sealed class NoOpActivationCommand : IAllowanceWindowActivationCommand
    {
        public Task SendHiAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DisabledActivationSettings : IAllowanceWindowActivationSettings
    {
        public bool ActivationEnabled { get; set; }
        public bool NotificationsEnabled { get; set; }
        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window) => null;
        public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset)
        {
        }
    }
}
