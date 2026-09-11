using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public async Task UpdateAttemptsActivationForUnusedWindowRegardlessOfResetTime()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var command = new ActivationRecordingCommand();
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now, 0, now.AddHours(5))),
            command,
            new ActivationSettings(),
            new ActivationTimeProvider(now));

        await updates.RefreshAsync();

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task ChangedResetTimeConfirmsActivationWithoutAnotherCommand()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var activatedReset = originalReset.AddMinutes(1);
        var settings = new ActivationSettings();
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using (var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, originalReset),
                ObserveAllowance(now.AddMinutes(1), 0, activatedReset)),
            command,
            settings,
            time))
        {
            await updates.RefreshAsync();
            time.Advance(TimeSpan.FromMinutes(1));
            await updates.RefreshAsync();
        }

        var afterRestartCommand = new ActivationRecordingCommand();
        await using var afterRestart = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now.AddMinutes(1), 0, activatedReset)),
            afterRestartCommand,
            settings,
            time);
        await afterRestart.RefreshAsync();

        Assert.Equal(1, command.CallCount);
        Assert.Equal(0, afterRestartCommand.CallCount);
        Assert.Equal(activatedReset, settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
    }

    [Fact]
    public async Task UnconfirmedActivationStopsAfterThreeRetries()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var observations = Enumerable.Range(0, 5)
            .Select(minute => ObserveAllowance(now.AddMinutes(minute), 0, reset))
            .ToArray();
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(observations),
            command,
            new ActivationSettings(),
            time);

        UsagePresentation? final = null;
        for (var attempt = 0; attempt < observations.Length; attempt++)
        {
            if (attempt > 0)
            {
                time.Advance(TimeSpan.FromMinutes(1));
            }

            final = await updates.RefreshAsync();
        }

        Assert.Equal(4, command.CallCount);
        Assert.Equal(
            "Could not confirm 5-hour allowance activation after four requests.",
            Assert.Single(final!.Notices).Message);
    }

    [Fact]
    public async Task FailedCommandStopsRetryingWhenRecoveryShowsUsedUpAllowance()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var reader = new ActivationObservationReader(
            ObserveAllowance(now, 0, reset),
            ObserveAllowance(now.AddSeconds(1), 100, reset),
            ObserveAllowance(now.AddMinutes(5), 100, reset));
        var command = new ActivationRecordingCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(reader, command, new ActivationSettings(), time);

        var recovered = await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        await updates.RefreshAsync();

        Assert.Equal(1, command.CallCount);
        Assert.Equal(3, reader.CallCount);
        Assert.Equal("0% left", recovered.Popup.FiveHour.RemainingText);
    }

    [Fact]
    public async Task UpdateReportsUsedUpAndNaturalResetTransitionsOnce()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var settings = new ActivationSettings
        {
            ActivationEnabled = false,
            NotificationsEnabled = true
        };
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 99, reset),
                ObserveAllowance(now.AddMinutes(1), 100, reset),
                ObserveAllowance(now.AddMinutes(2), 100, reset),
                ObserveAllowance(now.AddMinutes(3), 0, reset.AddHours(5))),
            new ActivationRecordingCommand(),
            settings,
            new ActivationTimeProvider(now));

        var initial = await updates.RefreshAsync();
        var usedUp = await updates.RefreshAsync();
        var repeated = await updates.RefreshAsync();
        var naturalReset = await updates.RefreshAsync();

        Assert.Empty(initial.Notices);
        Assert.Equal("5-hour allowance used up.", Assert.Single(usedUp.Notices).Message);
        Assert.Empty(repeated.Notices);
        Assert.Equal("5-hour allowance reset.", Assert.Single(naturalReset.Notices).Message);
    }

    [Fact]
    public async Task ConcurrentUpdatesShareOneActivationCommand()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new ActivationBlockingCommand();
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, reset),
                ObserveAllowance(now, 0, reset)),
            command,
            new ActivationSettings(),
            new ActivationTimeProvider(now));

        var first = updates.RefreshAsync();
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = updates.RefreshAsync();
        command.Completion.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task OneCommandTargetsEitherUnusedWindowAndRetriesPartialConfirmation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var fiveHourReset = now.AddHours(5);
        var weeklyReset = now.AddDays(7);
        var settings = new ActivationSettings();
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, fiveHourReset, 0, weeklyReset),
                ObserveAllowance(now.AddMinutes(1), 0, fiveHourReset.AddMinutes(1), 0, weeklyReset),
                ObserveAllowance(now.AddMinutes(2), 1, fiveHourReset.AddMinutes(1), 0, weeklyReset.AddMinutes(2))),
            command,
            settings,
            time);

        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshAsync();

        Assert.Equal(2, command.CallCount);
        Assert.Equal(fiveHourReset.AddMinutes(1), settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.Equal(weeklyReset.AddMinutes(2), settings.ReadActivatedReset(AllowanceWindowKind.Weekly));
    }

    [Fact]
    public async Task CommandFailureRetriesAfterFiveMinutesWithoutUsingActivationAttempt()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new ActivationRecordingCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, reset),
                ObserveAllowance(now.AddSeconds(1), 0, reset),
                ObserveAllowance(now.AddMinutes(4), 0, reset),
                ObserveAllowance(now.AddMinutes(5), 0, reset)),
            command,
            new ActivationSettings(),
            time);

        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(4));
        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshAsync();

        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task FailedRecoveryRefreshUsesFiveMinuteBackoff()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var reader = new ActivationObservationReader();
        reader.Enqueue(ObserveAllowance(now, 0, reset));
        reader.EnqueueFailure(new IOException("synthetic refresh failure"));
        reader.Enqueue(ObserveAllowance(now.AddMinutes(4), 0, reset));
        reader.Enqueue(ObserveAllowance(now.AddMinutes(5), 0, reset));
        var command = new ActivationRecordingCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(reader, command, new ActivationSettings(), time);

        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(4));
        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshAsync();

        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task SuccessfulAttemptStopsRetryingWhileAllowanceIsUsedUp()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, reset),
                ObserveAllowance(now.AddMinutes(1), 100, reset)),
            command,
            new ActivationSettings(),
            time);

        await updates.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshAsync();

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task ResetChangeDuringFailedRetryConfirmsActivation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var changedReset = originalReset.AddMinutes(1);
        var settings = new ActivationSettings();
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, originalReset),
                ObserveAllowance(now.AddMinutes(1), 0, originalReset),
                ObserveAllowance(now.AddMinutes(1).AddSeconds(1), 1, changedReset)),
            command,
            settings,
            time);

        await updates.RefreshAsync();
        command.FailNext(new InvalidOperationException("synthetic retry failure"));
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshAsync();

        Assert.Equal(2, command.CallCount);
        Assert.Equal(changedReset, settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
    }

    [Fact]
    public async Task ResetChangeAfterFailedInitialCommandConfirmsActivation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var changedReset = originalReset.AddMinutes(1);
        var settings = new ActivationSettings();
        var command = new ActivationRecordingCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, originalReset),
                ObserveAllowance(now.AddSeconds(1), 1, changedReset)),
            command,
            settings,
            new ActivationTimeProvider(now));

        await updates.RefreshAsync();

        Assert.Equal(1, command.CallCount);
        Assert.Equal(changedReset, settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
    }

    [Fact]
    public async Task RetryStateDoesNotSurviveRestart()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var settings = new ActivationSettings();
        var firstCommand = new ActivationRecordingCommand();
        await using (var first = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now, 0, reset)),
            firstCommand,
            settings,
            new ActivationTimeProvider(now)))
        {
            await first.RefreshAsync();
        }

        var restartedCommand = new ActivationRecordingCommand();
        await using var restarted = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now, 0, reset)),
            restartedCommand,
            settings,
            new ActivationTimeProvider(now));
        await restarted.RefreshAsync();

        Assert.Equal(1, firstCommand.CallCount);
        Assert.Equal(1, restartedCommand.CallCount);
    }

    [Fact]
    public async Task UnusedWindowWithoutResetTimeDoesNotActivate()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var command = new ActivationRecordingCommand();
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now, 0, fiveHourReset: null)),
            command,
            new ActivationSettings(),
            new ActivationTimeProvider(now));

        await updates.RefreshAsync();

        Assert.Equal(0, command.CallCount);
    }

    [Fact]
    public async Task SimultaneousTransitionsProduceCombinedNotices()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var fiveHourReset = now.AddHours(5);
        var weeklyReset = now.AddDays(7);
        var settings = new ActivationSettings
        {
            ActivationEnabled = false,
            NotificationsEnabled = true
        };
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 99, fiveHourReset, 99, weeklyReset),
                ObserveAllowance(now.AddMinutes(1), 100, fiveHourReset, 100, weeklyReset),
                ObserveAllowance(now.AddMinutes(2), 0, fiveHourReset.AddHours(5), 0, weeklyReset.AddDays(7))),
            new ActivationRecordingCommand(),
            settings,
            new ActivationTimeProvider(now));

        await updates.RefreshAsync();
        var usedUp = await updates.RefreshAsync();
        var reset = await updates.RefreshAsync();

        Assert.Equal("5-hour and weekly allowance used up.", Assert.Single(usedUp.Notices).Message);
        Assert.Equal("5-hour and weekly allowance reset.", Assert.Single(reset.Notices).Message);
    }

    private static UsageUpdates CreateActivationUpdates(
        IUsageObservationReader observations,
        IAllowanceWindowActivationCommand command,
        IAllowanceWindowActivationSettings settings,
        TimeProvider timeProvider) =>
        new(observations, command, settings, timeProvider, CultureInfo.InvariantCulture);

    private static UsageObservations ObserveAllowance(
        DateTimeOffset observedAt,
        int fiveHourUsedPercent,
        DateTimeOffset? fiveHourReset,
        int? weeklyUsedPercent = null,
        DateTimeOffset? weeklyReset = null) =>
        new(
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
                new AccountActivityObservation.NotRequested()),
            Local: null);

    private sealed class ActivationObservationReader : IUsageObservationReader
    {
        private readonly Queue<Func<UsageObservations>> reads = [];

        public ActivationObservationReader(params UsageObservations[] observations)
        {
            foreach (var observation in observations)
            {
                Enqueue(observation);
            }
        }

        public int CallCount { get; private set; }

        public void Enqueue(UsageObservations observation) => reads.Enqueue(() => observation);

        public void EnqueueFailure(Exception exception) => reads.Enqueue(
            () => throw exception);

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(UsageObservationRequest.AllowanceWindows, request);
            CallCount++;
            return Task.FromResult(reads.Dequeue()());
        }
    }

    private sealed class ActivationRecordingCommand : IAllowanceWindowActivationCommand
    {
        private readonly Queue<Exception> failures = [];

        public int CallCount { get; private set; }

        public void FailNext(Exception exception) => failures.Enqueue(exception);

        public Task SendHiAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return failures.TryDequeue(out var failure)
                ? Task.FromException(failure)
                : Task.CompletedTask;
        }
    }

    private sealed class ActivationBlockingCommand : IAllowanceWindowActivationCommand
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

    private sealed class ActivationSettings : IAllowanceWindowActivationSettings
    {
        private readonly Dictionary<AllowanceWindowKind, DateTimeOffset> activatedResets = [];

        public bool ActivationEnabled { get; set; } = true;
        public bool NotificationsEnabled { get; set; }

        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window) =>
            activatedResets.GetValueOrDefault(window);

        public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset) =>
            activatedResets[window] = reset;
    }

    private sealed class ActivationTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
