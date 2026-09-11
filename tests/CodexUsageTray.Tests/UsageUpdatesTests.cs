using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class UsageUpdatesTests
{
    [Fact]
    public async Task RefreshPublishesSnapshotFromActivationRecoveryRefresh()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var observations = new QueueUsageObservationReader(
            Observe(now, usedPercent: 0, reset),
            Observe(now.AddSeconds(1), usedPercent: 100, reset));
        var snapshots = new UsageSnapshots(observations);
        var time = new FixedTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            new FailingActivationCommand(),
            snapshots,
            new EnabledActivationSettings(),
            time);
        await using var updates = new UsageUpdates(
            snapshots,
            activation,
            time,
            CultureInfo.InvariantCulture);

        var update = await updates.RefreshAsync();

        Assert.Equal(100, update.Snapshot.FiveHour?.UsedPercent);
        Assert.Equal("0% left", update.Presentation.Popup.FiveHour.RemainingText);
        Assert.Equal(AllowanceWindows.FiveHour, update.AllowanceEvents.UsedUp);
    }

    [Fact]
    public async Task ConcurrentRefreshesPublishInRequestOrder()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var observations = new QueueUsageObservationReader(
            Observe(now, usedPercent: 0, reset),
            Observe(now.AddSeconds(1), usedPercent: 10, reset));
        var snapshots = new UsageSnapshots(observations);
        var command = new BlockingActivationCommand();
        var time = new FixedTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            snapshots,
            new EnabledActivationSettings(),
            time);
        await using var updates = new UsageUpdates(
            snapshots,
            activation,
            time,
            CultureInfo.InvariantCulture);

        var firstRefresh = updates.RefreshAsync();
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRefresh = updates.RefreshAsync();
        command.Completion.TrySetResult();

        var first = await firstRefresh;
        var second = await secondRefresh;

        Assert.Equal(0, first.Snapshot.FiveHour?.UsedPercent);
        Assert.Equal(10, second.Snapshot.FiveHour?.UsedPercent);
    }

    [Fact]
    public async Task DisposalCancelsActiveAndQueuedRefreshes()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var observations = new BlockingUsageObservationReader();
        var snapshots = new UsageSnapshots(observations);
        var time = new FixedTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            new FailingActivationCommand(),
            snapshots,
            new EnabledActivationSettings { ActivationEnabled = false },
            time);
        var updates = new UsageUpdates(
            snapshots,
            activation,
            time,
            CultureInfo.InvariantCulture);

        var activeRefresh = updates.RefreshAsync();
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRefresh = updates.RefreshAsync();
        var disposal = updates.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeRefresh);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedRefresh);
        await disposal;
    }

    [Fact]
    public async Task ActivityRefreshPublishesOnePresentationOfObservedActivity()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var observation = new UsageObservations(
            new AccountUsageObservation(
                now,
                [new AllowanceWindow(25, TimeSpan.FromHours(5), reset)],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 5678,
                    TodayTokens: 1234,
                    LatestDailyBucketDate: DateOnly.FromDateTime(now.LocalDateTime))),
            Local: null);
        var snapshots = new UsageSnapshots(new QueueUsageObservationReader(observation));
        var time = new FixedTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            new FailingActivationCommand(),
            snapshots,
            new EnabledActivationSettings { ActivationEnabled = false },
            time);
        await using var updates = new UsageUpdates(
            snapshots,
            activation,
            time,
            CultureInfo.InvariantCulture);

        var update = await updates.RefreshWithActivityAsync();

        Assert.Equal(1234, update.Snapshot.TodayTokens);
        Assert.Equal("1.2K tokens", update.Presentation.Popup.TodayTokens);
        Assert.Equal(75, update.Presentation.Tray.FiveHourRemaining);
    }

    private static UsageObservations Observe(
        DateTimeOffset observedAt,
        int usedPercent,
        DateTimeOffset reset) =>
        new(
            new AccountUsageObservation(
                observedAt,
                [new AllowanceWindow(usedPercent, TimeSpan.FromHours(5), reset)],
                "plus",
                "Codex",
                new AccountActivityObservation.NotRequested()),
            Local: null);

    private sealed class QueueUsageObservationReader(params UsageObservations[] observations)
        : IUsageObservationReader
    {
        private readonly Queue<UsageObservations> observations = new(observations);

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            var observation = observations.Dequeue();
            var expected = observation.Account.Activity is AccountActivityObservation.NotRequested
                ? UsageObservationRequest.AllowanceWindows
                : UsageObservationRequest.AllowanceWindowsAndActivity;
            Assert.Equal(expected, request);
            return Task.FromResult(observation);
        }
    }

    private sealed class BlockingUsageObservationReader : IUsageObservationReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FailingActivationCommand : IAllowanceWindowActivationCommand
    {
        public Task SendHiAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Synthetic activation failure."));
    }

    private sealed class BlockingActivationCommand : IAllowanceWindowActivationCommand
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendHiAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class EnabledActivationSettings : IAllowanceWindowActivationSettings
    {
        private readonly Dictionary<AllowanceWindowKind, DateTimeOffset> activatedResets = [];

        public bool ActivationEnabled { get; set; } = true;
        public bool NotificationsEnabled { get; set; }

        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window) =>
            activatedResets.GetValueOrDefault(window);

        public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset) =>
            activatedResets[window] = reset;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
