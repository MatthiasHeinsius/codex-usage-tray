using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public async Task CallerCancellationCancelsObservationAndNextUpdateStartsFresh()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var canceledRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var nextRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        using var cancellation = new CancellationTokenSource();
        await using var updates = CreateUsageUpdates(observations);

        var canceledUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token);
        await canceledRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledUpdate);
        Assert.True(canceledRead.AdapterCancellation.IsCancellationRequested);

        var nextUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await nextRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        nextRead.Succeed(CreateObservations(observedAt, usedPercent: 20));

        Assert.Equal("80% left", (await nextUpdate).Popup.FiveHour.RemainingText);
        Assert.Equal(2, observations.Requests.Length);
        Assert.Equal(1, observations.MaximumConcurrentReads);
    }

    [Fact]
    public async Task QueuedActivityUpdateWaitsForRoutineUpdateThenReadsActivity()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        await using var updates = CreateUsageUpdates(observations);

        var routineUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var activityUpdate = updates.RequestAsync(UsageUpdateIntent.Activity, TestContext.Current.CancellationToken);
        Assert.False(activityRead.Started.Task.IsCompleted);

        routineRead.Succeed(CreateObservations(observedAt, usedPercent: 20));
        Assert.Equal("80% left", (await routineUpdate).Popup.FiveHour.RemainingText);
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        activityRead.Succeed(CreateObservations(observedAt.AddSeconds(1), usedPercent: 21, includeActivity: true));

        Assert.Equal("1.2K tokens", (await activityUpdate).Popup.TodayTokens);
        Assert.Equal(
            [UsageObservationRequest.AllowanceWindows, UsageObservationRequest.AllowanceWindowsAndActivity],
            observations.Requests);
        Assert.Equal(1, observations.MaximumConcurrentReads);
    }

    [Fact]
    public async Task QueuedActivityUpdateStillRunsWhenRoutineObservationFails()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var activityRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        await using var updates = CreateUsageUpdates(observations);

        var routineUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var activityUpdate = updates.RequestAsync(UsageUpdateIntent.Activity, TestContext.Current.CancellationToken);
        routineRead.Fail(new IOException("routine observation failed"));
        await Assert.ThrowsAsync<IOException>(() => routineUpdate);
        await activityRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        activityRead.Succeed(CreateObservations(observedAt, usedPercent: 21, includeActivity: true));

        Assert.Equal("1.2K tokens", (await activityUpdate).Popup.TodayTokens);
        Assert.Equal(2, observations.Requests.Length);
    }

    [Fact]
    public async Task CanceledQueuedActivityUpdateDoesNotRequestActivity()
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var routineRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        var nextRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        using var cancellation = new CancellationTokenSource();
        await using var updates = CreateUsageUpdates(observations);

        var routineUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await routineRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var canceledActivity = updates.RequestAsync(UsageUpdateIntent.Activity, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledActivity);
        Assert.False(routineRead.AdapterCancellation.IsCancellationRequested);
        routineRead.Succeed(CreateObservations(observedAt, usedPercent: 20));
        await routineUpdate;

        var nextUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await nextRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        nextRead.Succeed(CreateObservations(observedAt.AddMinutes(1), usedPercent: 30));
        Assert.Equal("70% left", (await nextUpdate).Popup.FiveHour.RemainingText);
        Assert.Equal(
            [UsageObservationRequest.AllowanceWindows, UsageObservationRequest.AllowanceWindows],
            observations.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedObservationLetsNextUpdateStartFresh(bool observationCanceled)
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var failedRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity);
        var nextRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        await using var updates = CreateUsageUpdates(observations);

        var failedUpdate = updates.RequestAsync(UsageUpdateIntent.Activity, TestContext.Current.CancellationToken);
        await failedRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Exception observationFailure = observationCanceled
            ? new OperationCanceledException("activity observation failed")
            : new IOException("activity observation failed");
        failedRead.Fail(observationFailure);
        Assert.Same(observationFailure, await Assert.ThrowsAnyAsync<Exception>(() => failedUpdate));

        var nextUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await nextRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        nextRead.Succeed(CreateObservations(observedAt, usedPercent: 30));
        Assert.Equal("70% left", (await nextUpdate).Popup.FiveHour.RemainingText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWaitsForObservationCleanupAndDiscardsItsResult(bool dispose)
    {
        var observedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(2));
        var observations = new ScriptedObservationReader();
        var activeRead = observations.Enqueue(UsageObservationRequest.AllowanceWindowsAndActivity, ignoreCancellation: true);
        var nextRead = observations.Enqueue(UsageObservationRequest.AllowanceWindows);
        using var cancellation = new CancellationTokenSource();
        await using var updates = CreateUsageUpdates(observations);

        var activeUpdate = updates.RequestAsync(UsageUpdateIntent.Activity, cancellation.Token);
        try
        {
            await activeRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var nextUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
            var disposal = dispose ? updates.DisposeAsync().AsTask() : null;
            if (!dispose)
            {
                cancellation.Cancel();
            }

            Assert.True(activeRead.AdapterCancellation.IsCancellationRequested);
            Assert.False(activeUpdate.IsCompleted);
            Assert.False(nextRead.Started.Task.IsCompleted);
            if (disposal is not null)
            {
                Assert.False(disposal.IsCompleted);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextUpdate);
            }

            activeRead.Succeed(CreateObservations(observedAt, usedPercent: 20, includeActivity: true));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeUpdate);
            if (disposal is not null)
            {
                await disposal;
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken));
            }
            else
            {
                await nextRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                nextRead.Succeed(CreateObservations(observedAt.AddMinutes(1), usedPercent: 30));
                var presentation = await nextUpdate;
                Assert.Equal("70% left", presentation.Popup.FiveHour.RemainingText);
                Assert.Equal("Unavailable", presentation.Popup.TodayTokens);
                Assert.Equal(1, observations.MaximumConcurrentReads);
            }
        }
        finally
        {
            // Release the cancellation-ignoring fixture even if an assertion fails.
            activeRead.Succeed(CreateObservations(observedAt, usedPercent: 20));
            nextRead.Succeed(CreateObservations(observedAt, usedPercent: 30));
        }
    }

    private static UsageUpdates CreateUsageUpdates(IUsageObservationReader observations) =>
        new(observations, new NoOpActivationCommand(), new DisabledActivationSettings(),
            TimeProvider.System, CultureInfo.InvariantCulture);

    private static UsageObservations CreateObservations(DateTimeOffset observedAt, int usedPercent, bool includeActivity = false)
    {
        var activity = includeActivity
            ? (AccountActivityObservation)new AccountActivityObservation.Observed(5678, 1234, DateOnly.FromDateTime(observedAt.LocalDateTime))
            : new AccountActivityObservation.NotRequested();
        return new UsageObservations(
            new AccountUsageObservation(observedAt,
                [new AllowanceWindow(usedPercent, TimeSpan.FromHours(5), observedAt.AddHours(1))],
                "plus", "Codex", activity), Local: null);
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

        public ObservationReadStep Enqueue(UsageObservationRequest expected, bool ignoreCancellation = false)
        {
            var step = new ObservationReadStep(expected, ignoreCancellation);
            steps.Enqueue(step);
            return step;
        }

        public async Task<UsageObservations> ReadAsync(UsageObservationRequest request, CancellationToken cancellationToken)
        {
            ObservationReadStep step;
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
                return await (step.IgnoreCancellation ? step.Completion.Task : step.Completion.Task.WaitAsync(cancellationToken));
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

    private sealed class ObservationReadStep(UsageObservationRequest expected, bool ignoreCancellation)
    {
        public UsageObservationRequest Expected { get; } = expected;
        public bool IgnoreCancellation { get; } = ignoreCancellation;
        public CancellationToken AdapterCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UsageObservations> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(UsageObservations observations) => Completion.TrySetResult(observations);
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}
