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
        var time = new FixedTimeProvider(now);
        var settings = new EnabledActivationSettings { NotificationsEnabled = true };
        await using var updates = new UsageUpdates(
            observations,
            new FailingActivationCommand(),
            settings,
            time,
            CultureInfo.InvariantCulture);

        var presentation = await updates.RefreshAsync();

        Assert.Equal("0% left", presentation.Popup.FiveHour.RemainingText);
        var notice = Assert.Single(presentation.Notices);
        Assert.Equal("5-hour allowance used up.", notice.Message);
        Assert.Equal(UsagePresentation.NoticeSeverity.Warning, notice.Severity);
        Assert.Equal(TimeSpan.FromSeconds(5), notice.Duration);
    }

    [Fact]
    public async Task ConcurrentRefreshesPublishInRequestOrder()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var observations = new QueueUsageObservationReader(
            Observe(now, usedPercent: 0, reset),
            Observe(now.AddSeconds(1), usedPercent: 10, reset));
        var command = new BlockingActivationCommand();
        var time = new FixedTimeProvider(now);
        await using var updates = new UsageUpdates(
            observations,
            command,
            new EnabledActivationSettings(),
            time,
            CultureInfo.InvariantCulture);

        var firstRefresh = updates.RefreshAsync();
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRefresh = updates.RefreshAsync();
        command.Completion.TrySetResult();

        var first = await firstRefresh;
        var second = await secondRefresh;

        Assert.Equal("100% left", first.Popup.FiveHour.RemainingText);
        Assert.Equal("90% left", second.Popup.FiveHour.RemainingText);
    }

    [Fact]
    public async Task DisposalCancelsActiveAndQueuedRefreshes()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var observations = new BlockingUsageObservationReader();
        var time = new FixedTimeProvider(now);
        var updates = new UsageUpdates(
            observations,
            new FailingActivationCommand(),
            new EnabledActivationSettings { ActivationEnabled = false },
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
        var time = new FixedTimeProvider(now);
        await using var updates = new UsageUpdates(
            new QueueUsageObservationReader(observation),
            new FailingActivationCommand(),
            new EnabledActivationSettings { ActivationEnabled = false },
            time,
            CultureInfo.InvariantCulture);

        var presentation = await updates.RefreshWithActivityAsync();

        Assert.Equal("1.2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal(75, presentation.Tray.FiveHourRemaining);
    }

    [Fact]
    public async Task PreferencesArePersistedAndCapturedWhenRefreshIsRequested()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var changedReset = reset.AddHours(5);
        var observations = new QueueUsageObservationReader(
            Observe(now, usedPercent: 0, reset),
            Observe(now.AddMinutes(1), usedPercent: 100, reset),
            Observe(now.AddMinutes(2), usedPercent: 0, changedReset));
        var command = new BlockingActivationCommand();
        var settings = new EnabledActivationSettings { NotificationsEnabled = false };
        await using var updates = new UsageUpdates(
            observations,
            command,
            settings,
            new FixedTimeProvider(now),
            CultureInfo.InvariantCulture);

        var firstRefresh = updates.RefreshAsync();
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRefresh = updates.RefreshAsync();
        updates.NotificationsEnabled = true;
        command.Completion.TrySetResult();

        await firstRefresh;
        var queuedPresentation = await queuedRefresh;
        var nextPresentation = await updates.RefreshAsync();

        Assert.True(settings.NotificationsEnabled);
        Assert.Empty(queuedPresentation.Notices);
        var notice = Assert.Single(nextPresentation.Notices);
        Assert.Equal("5-hour allowance reset.", notice.Message);
    }

    [Fact]
    public async Task ActivationPreferenceIsCapturedWhenRefreshIsRequested()
    {
        var now = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var observations = new BlockingFirstUsageObservationReader(
            Observe(now, usedPercent: 50, reset),
            Observe(now.AddMinutes(1), usedPercent: 0, reset),
            Observe(now.AddMinutes(2), usedPercent: 0, reset));
        var command = new RecordingActivationCommand();
        var settings = new EnabledActivationSettings { ActivationEnabled = false };
        await using var updates = new UsageUpdates(
            observations,
            command,
            settings,
            new FixedTimeProvider(now),
            CultureInfo.InvariantCulture);

        var firstRefresh = updates.RefreshAsync();
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRefresh = updates.RefreshAsync();
        updates.ActivationEnabled = true;
        observations.CompleteFirst();

        await firstRefresh;
        await queuedRefresh;
        Assert.Equal(0, command.CallCount);

        await updates.RefreshAsync();
        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task FailedPreferenceWriteKeepsTheEffectiveValue()
    {
        var settings = new EnabledActivationSettings
        {
            ActivationEnabled = false,
            ThrowOnActivationWrite = true
        };
        await using var updates = new UsageUpdates(
            new QueueUsageObservationReader(),
            new FailingActivationCommand(),
            settings,
            TimeProvider.System,
            CultureInfo.InvariantCulture);

        Assert.Throws<UnauthorizedAccessException>(() => updates.ActivationEnabled = true);

        Assert.False(updates.ActivationEnabled);
        Assert.False(settings.ActivationEnabled);
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

    private sealed class BlockingFirstUsageObservationReader(
        UsageObservations first,
        params UsageObservations[] remaining) : IUsageObservationReader
    {
        private readonly Queue<UsageObservations> remaining = new(remaining);
        private readonly TaskCompletionSource<UsageObservations> firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteFirst() => firstCompletion.TrySetResult(first);

        public async Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(UsageObservationRequest.AllowanceWindows, request);
            if (Interlocked.Increment(ref callCount) == 1)
            {
                Started.TrySetResult();
                return await firstCompletion.Task.WaitAsync(cancellationToken);
            }

            return remaining.Dequeue();
        }
    }

    private sealed class FailingActivationCommand : IAllowanceWindowActivationCommand
    {
        public Task SendHiAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Synthetic activation failure."));
    }

    private sealed class RecordingActivationCommand : IAllowanceWindowActivationCommand
    {
        public int CallCount { get; private set; }

        public Task SendHiAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
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
        private bool activationEnabled = true;

        public bool ThrowOnActivationWrite { get; set; }
        public bool ActivationEnabled
        {
            get => activationEnabled;
            set
            {
                if (ThrowOnActivationWrite)
                {
                    throw new UnauthorizedAccessException("Synthetic settings failure.");
                }

                activationEnabled = value;
            }
        }

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
