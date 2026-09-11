namespace CodexUsageTray.Tests;

public sealed class AllowanceWindowActivationTests
{
    [Fact]
    public async Task ObserveActivatesUnusedWindowRegardlessOfResetTime()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var settings = new InMemoryActivationSettings();
        var command = new RecordingActivationCommand();
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            settings,
            new TestTimeProvider(now));
        var snapshot = Snapshot(now, fiveHourUsedPercent: 0, fiveHourReset: now.AddHours(5));

        await activation.ObserveAsync(activationEnabled: true, snapshot);

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task ChangedResetTimeConfirmsActivationWithoutAnotherCommand()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var settings = new InMemoryActivationSettings();
        var command = new RecordingActivationCommand();
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            settings,
            time);
        var originalReset = now.AddHours(5);
        var activatedReset = originalReset.AddMinutes(1);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, originalReset));
        time.Advance(TimeSpan.FromMinutes(1));
        var activated = Snapshot(now.AddMinutes(1), 0, activatedReset);
        await activation.ObserveAsync(activationEnabled: true, activated);
        var afterRestartCommand = new RecordingActivationCommand();
        var afterRestart = new AllowanceWindowActivation(
            afterRestartCommand,
            new StubUsageSnapshotRefresher(),
            settings,
            time);
        await afterRestart.ObserveAsync(activationEnabled: true, activated);

        Assert.Equal(1, command.CallCount);
        Assert.Equal(0, afterRestartCommand.CallCount);
    }

    [Fact]
    public async Task UnconfirmedActivationStopsAfterThreeRetries()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var settings = new InMemoryActivationSettings();
        var command = new RecordingActivationCommand();
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            settings,
            time);
        var reset = now.AddHours(5);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, reset));
        for (var retry = 1; retry <= 3; retry++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(retry), 0, reset));
        }

        time.Advance(TimeSpan.FromMinutes(1));
        var result = await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(4), 0, reset));

        Assert.Equal(4, command.CallCount);
        Assert.Equal(AllowanceWindows.FiveHour, result.Unconfirmed);
    }

    [Fact]
    public async Task FailedCommandWaitsWhenFreshObservationShowsUsedUpAllowance()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var settings = new InMemoryActivationSettings();
        var command = new RecordingActivationCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var reset = now.AddHours(5);
        var refresher = new StubUsageSnapshotRefresher(Snapshot(now.AddSeconds(1), 100, reset));
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(command, refresher, settings, time);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, reset));
        time.Advance(TimeSpan.FromMinutes(5));
        await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(5), 100, reset));

        Assert.Equal(1, command.CallCount);
        Assert.Equal(1, refresher.CallCount);
    }

    [Fact]
    public async Task ObserveReportsUsedUpAndNaturalResetTransitionsOnce()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var activation = new AllowanceWindowActivation(
            new RecordingActivationCommand(),
            new StubUsageSnapshotRefresher(),
            new InMemoryActivationSettings(),
            new TestTimeProvider(now));
        var reset = now.AddHours(5);

        var initial = await activation.ObserveAsync(activationEnabled: false, Snapshot(now, 99, reset));
        var usedUp = await activation.ObserveAsync(activationEnabled: false, Snapshot(now.AddMinutes(1), 100, reset));
        var repeated = await activation.ObserveAsync(activationEnabled: false, Snapshot(now.AddMinutes(2), 100, reset));
        var naturalReset = await activation.ObserveAsync(activationEnabled: false, Snapshot(now.AddMinutes(3), 0, reset.AddHours(5)));

        Assert.Equal(AllowanceWindows.None, initial.UsedUp);
        Assert.Equal(AllowanceWindows.FiveHour, usedUp.UsedUp);
        Assert.Equal(AllowanceWindows.None, repeated.UsedUp);
        Assert.Equal(AllowanceWindows.FiveHour, naturalReset.Reset);
    }

    [Fact]
    public async Task ConcurrentObservationsShareOneActivationCommand()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var command = new BlockingActivationCommand();
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            new InMemoryActivationSettings(),
            new TestTimeProvider(now));
        var snapshot = Snapshot(now, 0, now.AddHours(5));

        var first = activation.ObserveAsync(activationEnabled: true, snapshot);
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = activation.ObserveAsync(activationEnabled: true, snapshot);
        command.Completion.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task OneCommandTargetsEitherUnusedWindowAndRetriesPartialConfirmation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var settings = new InMemoryActivationSettings();
        var command = new RecordingActivationCommand();
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            settings,
            time);
        var fiveHourReset = now.AddHours(5);
        var weeklyReset = now.AddDays(7);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, fiveHourReset, 0, weeklyReset));
        time.Advance(TimeSpan.FromMinutes(1));
        var partial = await activation.ObserveAsync(activationEnabled: true,
            Snapshot(now.AddMinutes(1), 0, fiveHourReset.AddMinutes(1), 0, weeklyReset));
        time.Advance(TimeSpan.FromMinutes(1));
        var complete = await activation.ObserveAsync(activationEnabled: true,
            Snapshot(now.AddMinutes(2), 1, fiveHourReset.AddMinutes(1), 0, weeklyReset.AddMinutes(2)));

        Assert.Equal(2, command.CallCount);
        Assert.Equal(AllowanceWindows.FiveHour, partial.Confirmed);
        Assert.Equal(AllowanceWindows.Weekly, complete.Confirmed);
    }

    [Fact]
    public async Task OtherCommandFailureRetriesAfterFiveMinutesWithoutUsingActivationAttempt()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new RecordingActivationCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(Snapshot(now.AddSeconds(1), 0, reset)),
            new InMemoryActivationSettings(),
            time);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, reset));
        time.Advance(TimeSpan.FromMinutes(4));
        await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(4), 0, reset));
        time.Advance(TimeSpan.FromMinutes(1));
        await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(5), 0, reset));

        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task FailedClassificationRefreshUsesFiveMinuteBackoff()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new RecordingActivationCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new FailingUsageSnapshotRefresher(),
            new InMemoryActivationSettings(),
            time);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, reset));
        time.Advance(TimeSpan.FromMinutes(4));
        await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(4), 0, reset));
        time.Advance(TimeSpan.FromMinutes(1));
        await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(5), 0, reset));

        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task SuccessfulAttemptStopsRetryingWhileAllowanceIsUsedUp()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new RecordingActivationCommand();
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            new InMemoryActivationSettings(),
            time);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, reset));
        time.Advance(TimeSpan.FromMinutes(1));
        await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(1), 100, reset));

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task ExternalResetChangeDuringFailedRetryConfirmsActivation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var changedReset = originalReset.AddMinutes(1);
        var command = new RecordingActivationCommand();
        var time = new TestTimeProvider(now);
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(Snapshot(now.AddMinutes(1).AddSeconds(1), 1, changedReset)),
            new InMemoryActivationSettings(),
            time);

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, originalReset));
        command.FailNext(new InvalidOperationException("synthetic retry failure"));
        time.Advance(TimeSpan.FromMinutes(1));
        var result = await activation.ObserveAsync(activationEnabled: true, Snapshot(now.AddMinutes(1), 0, originalReset));

        Assert.Equal(AllowanceWindows.FiveHour, result.Confirmed);
        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task ExternalResetChangeAfterFailedInitialCommandConfirmsActivation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var changedReset = originalReset.AddMinutes(1);
        var command = new RecordingActivationCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(Snapshot(now.AddSeconds(1), 1, changedReset)),
            new InMemoryActivationSettings(),
            new TestTimeProvider(now));

        var result = await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, originalReset));

        Assert.Equal(AllowanceWindows.FiveHour, result.Confirmed);
        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task RetryStateDoesNotSurviveRestart()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var settings = new InMemoryActivationSettings();
        var snapshot = Snapshot(now, 0, now.AddHours(5));
        var firstCommand = new RecordingActivationCommand();
        var first = new AllowanceWindowActivation(
            firstCommand,
            new StubUsageSnapshotRefresher(),
            settings,
            new TestTimeProvider(now));
        await first.ObserveAsync(activationEnabled: true, snapshot);

        var restartedCommand = new RecordingActivationCommand();
        var restarted = new AllowanceWindowActivation(
            restartedCommand,
            new StubUsageSnapshotRefresher(),
            settings,
            new TestTimeProvider(now));
        await restarted.ObserveAsync(activationEnabled: true, snapshot);

        Assert.Equal(1, firstCommand.CallCount);
        Assert.Equal(1, restartedCommand.CallCount);
    }

    [Fact]
    public async Task UnusedWindowWithoutResetTimeDoesNotActivate()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var command = new RecordingActivationCommand();
        var activation = new AllowanceWindowActivation(
            command,
            new StubUsageSnapshotRefresher(),
            new InMemoryActivationSettings(),
            new TestTimeProvider(now));

        await activation.ObserveAsync(activationEnabled: true, Snapshot(now, 0, fiveHourReset: null));

        Assert.Equal(0, command.CallCount);
    }

    [Fact]
    public async Task SimultaneousTransitionsAreCombined()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var activation = new AllowanceWindowActivation(
            new RecordingActivationCommand(),
            new StubUsageSnapshotRefresher(),
            new InMemoryActivationSettings(),
            new TestTimeProvider(now));
        var fiveHourReset = now.AddHours(5);
        var weeklyReset = now.AddDays(7);

        await activation.ObserveAsync(activationEnabled: false, Snapshot(now, 99, fiveHourReset, 99, weeklyReset));
        var usedUp = await activation.ObserveAsync(activationEnabled: false,
            Snapshot(now.AddMinutes(1), 100, fiveHourReset, 100, weeklyReset));
        var reset = await activation.ObserveAsync(activationEnabled: false,
            Snapshot(now.AddMinutes(2), 0, fiveHourReset.AddHours(5), 0, weeklyReset.AddDays(7)));

        var both = AllowanceWindows.FiveHour | AllowanceWindows.Weekly;
        Assert.Equal(both, usedUp.UsedUp);
        Assert.Equal(both, reset.Reset);
    }

    private static UsageSnapshot Snapshot(
        DateTimeOffset observedAt,
        int fiveHourUsedPercent,
        DateTimeOffset? fiveHourReset,
        int? weeklyUsedPercent = null,
        DateTimeOffset? weeklyReset = null) =>
        UsageSnapshotFixture.Create(
            new AccountUsageObservation(
                observedAt,
                new[]
                {
                    new AllowanceWindow(fiveHourUsedPercent, TimeSpan.FromHours(5), fiveHourReset),
                    weeklyUsedPercent is { } used && weeklyReset is { } reset
                        ? new AllowanceWindow(used, TimeSpan.FromDays(7), reset)
                        : null
                }.OfType<AllowanceWindow>().ToArray(),
                "plus",
                "Codex",
                new AccountActivityObservation.NotRequested()));

    private sealed class RecordingActivationCommand : IAllowanceWindowActivationCommand
    {
        private readonly Queue<Exception> failures = [];

        public int CallCount { get; private set; }

        public void FailNext(Exception exception) => failures.Enqueue(exception);

        public Task SendHiAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (failures.TryDequeue(out var failure))
            {
                return Task.FromException(failure);
            }

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

    private sealed class StubUsageSnapshotRefresher(params UsageSnapshot[] snapshots) : IUsageSnapshotRefresher
    {
        private readonly Queue<UsageSnapshot> snapshots = new(snapshots);

        public int CallCount { get; private set; }

        public Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(snapshots.Dequeue());
        }
    }

    private sealed class FailingUsageSnapshotRefresher : IUsageSnapshotRefresher
    {
        public Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<UsageSnapshot>(new IOException("synthetic refresh failure"));
    }

    private sealed class InMemoryActivationSettings : IAllowanceWindowActivationSettings
    {
        private readonly Dictionary<AllowanceWindowKind, DateTimeOffset> activatedResets = [];

        public bool ActivationEnabled { get; set; }
        public bool NotificationsEnabled { get; set; }

        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window) =>
            activatedResets.GetValueOrDefault(window);

        public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset) =>
            activatedResets[window] = reset;
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
