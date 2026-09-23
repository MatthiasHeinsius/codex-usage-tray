using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public async Task UpdatePublishesSnapshotFromActivationRecoveryObservation()
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

        var presentation = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal("0% left", presentation.Popup.FiveHour.RemainingText);
        var notice = Assert.Single(presentation.Notices);
        Assert.Equal("5-hour allowance used up.", notice.Message);
        Assert.Equal(UsagePresentation.NoticeSeverity.Warning, notice.Severity);
        Assert.Equal(TimeSpan.FromSeconds(5), notice.Duration);
    }

    [Fact]
    public async Task ConcurrentUpdatesDoNotOverlap()
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

        var firstUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var secondUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        try
        {
            Assert.Single(observations.Requests);
            Assert.False(secondUpdate.IsCompleted);
        }
        finally
        {
            command.Completion.TrySetResult();
        }

        var first = await firstUpdate;
        var second = await secondUpdate;

        Assert.Equal("100% left", first.Popup.FiveHour.RemainingText);
        Assert.Equal("90% left", second.Popup.FiveHour.RemainingText);
    }

    [Fact]
    public async Task DisposalCancelsActiveAndQueuedUpdates()
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

        var activeUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queuedUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var disposal = updates.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeUpdate);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedUpdate);
        await disposal;
    }

    [Fact]
    public async Task PreferencesArePersistedAndCapturedWhenUpdateIsRequested()
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

        var firstUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queuedUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        updates.NotificationsEnabled = true;
        command.Completion.TrySetResult();

        await firstUpdate;
        var queuedPresentation = await queuedUpdate;
        var nextPresentation = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.True(settings.NotificationsEnabled);
        Assert.Empty(queuedPresentation.Notices);
        var notice = Assert.Single(nextPresentation.Notices);
        Assert.Equal("5-hour allowance reset.", notice.Message);
    }

    [Fact]
    public async Task ActivationPreferenceIsCapturedWhenUpdateIsRequested()
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

        var firstUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queuedUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        updates.ActivationEnabled = true;
        observations.CompleteFirst();

        await firstUpdate;
        await queuedUpdate;
        Assert.Equal(0, command.CallCount);

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task FailedNotificationWriteKeepsTheEffectiveValue()
    {
        var settings = new EnabledActivationSettings
        {
            NotificationsEnabled = false,
            ThrowOnNotificationWrite = true
        };
        await using var updates = new UsageUpdates(
            new QueueUsageObservationReader(),
            new FailingActivationCommand(),
            settings,
            TimeProvider.System,
            CultureInfo.InvariantCulture);

        Assert.Throws<UnauthorizedAccessException>(() => updates.NotificationsEnabled = true);

        Assert.False(updates.NotificationsEnabled);
        Assert.False(settings.NotificationsEnabled);
    }

    [Fact]
    public async Task UnknownIntentIsRejectedWithoutRequestingAnObservation()
    {
        var observations = new QueueUsageObservationReader();
        await using var updates = new UsageUpdates(
            observations,
            new FailingActivationCommand(),
            new EnabledActivationSettings(),
            TimeProvider.System,
            CultureInfo.InvariantCulture);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            updates.RequestAsync((UsageUpdateIntent)42, TestContext.Current.CancellationToken));

        Assert.Empty(observations.Requests);
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
        private readonly List<UsageObservationRequest> requests = [];

        public UsageObservationRequest[] Requests => requests.ToArray();

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
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
        public int CallCount { get; private set; }

        public async Task SendHiAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class EnabledActivationSettings : IAllowanceWindowActivationSettings
    {
        private readonly Dictionary<AllowanceWindowKind, DateTimeOffset> activatedResets = [];
        private bool activationEnabled = true;
        private bool notificationsEnabled;

        public bool ThrowOnActivationWrite { get; set; }
        public bool ThrowOnNotificationWrite { get; set; }
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

        public bool NotificationsEnabled
        {
            get => notificationsEnabled;
            set
            {
                if (ThrowOnNotificationWrite)
                {
                    throw new UnauthorizedAccessException("Synthetic settings failure.");
                }

                notificationsEnabled = value;
            }
        }

        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window) =>
            activatedResets.TryGetValue(window, out var reset) ? reset : null;

        public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset) =>
            activatedResets[window] = reset;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
