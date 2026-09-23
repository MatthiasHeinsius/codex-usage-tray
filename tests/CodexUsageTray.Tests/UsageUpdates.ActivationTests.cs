using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public async Task CancellationDuringActivationAllowsTheNextUpdateToStartFresh()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var observation = ObserveAllowance(now, 0, now.AddHours(5));
        var reader = new ActivationObservationReader(observation, observation);
        var command = new BlockingActivationCommand();
        var settings = new ActivationSettings();
        await using var updates = CreateActivationUpdates(reader, command, settings, new ActivationTimeProvider(now));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var canceledUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token);
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledUpdate);
        Assert.Equal(1, reader.CallCount);
        Assert.Equal(1, command.CallCount);
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));

        command.Completion.TrySetResult();
        var next = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(2, reader.CallCount);
        Assert.Equal(2, command.CallCount);
        Assert.Null(next.Popup.ActivityIndicator.ActivatedResetAt);
    }

    [Fact]
    public async Task CancellationDuringActivationRecoveryDoesNotImposeFailureBackoff()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var observation = ObserveAllowance(now, 0, now.AddHours(5));
        var recovery = new BlockingUsageObservationReader();
        var reader = new ActivationObservationReader(observation);
        reader.EnqueueRead(token => recovery.ReadAsync(UsageObservationRequest.AllowanceWindows, token));
        reader.Enqueue(observation);
        var command = new ActivationRecordingCommand();
        command.FailNext(new IOException("Activation failed."));
        var settings = new ActivationSettings();
        await using var updates = CreateActivationUpdates(reader, command, settings, new ActivationTimeProvider(now));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var canceledUpdate = updates.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token);
        await recovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledUpdate);
        Assert.Equal(2, reader.CallCount);
        Assert.Equal(1, command.CallCount);
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));

        var next = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(3, reader.CallCount);
        Assert.Equal(2, command.CallCount);
        Assert.Null(next.Popup.ActivityIndicator.ActivatedResetAt);
    }

    [Fact]
    public async Task UpdateAttemptsActivationForUnusedWindowBeforeItsResetTime()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var command = new ActivationRecordingCommand();
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now, 0, now.AddHours(5))),
            command,
            new ActivationSettings(),
            new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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
            await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromMinutes(1));
            var confirmed = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
            Assert.Equal(activatedReset, confirmed.Popup.ActivityIndicator.ActivatedResetAt);
        }

        var afterRestartCommand = new ActivationRecordingCommand();
        await using var afterRestart = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now.AddMinutes(1), 0, activatedReset)),
            afterRestartCommand,
            settings,
            time);
        var resumedPresentation = await afterRestart.RequestAsync(
            UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(1, command.CallCount);
        Assert.Equal(0, afterRestartCommand.CallCount);
        Assert.Equal(activatedReset, settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.Equal(activatedReset, resumedPresentation.Popup.ActivityIndicator.ActivatedResetAt);
    }

    [Fact]
    public async Task ActivationStopsAfterThreeUnconfirmedRetries()
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

        for (var attempt = 0; attempt < observations.Length; attempt++)
        {
            if (attempt > 0)
            {
                time.Advance(TimeSpan.FromMinutes(1));
            }

            await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        }

        Assert.Equal(4, command.CallCount);
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

        var recovered = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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

        var initial = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var usedUp = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var repeated = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var naturalReset = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Empty(initial.Notices);
        Assert.Equal("5-hour allowance used up.", Assert.Single(usedUp.Notices).Message);
        Assert.Empty(repeated.Notices);
        Assert.Equal("5-hour allowance reset.", Assert.Single(naturalReset.Notices).Message);
    }

    [Fact]
    public async Task AccountSwitchDoesNotReportAnAllowanceTransition()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var settings = new ActivationSettings { ActivationEnabled = false, NotificationsEnabled = true };
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 99, reset, accountEmail: "first@example.com"),
                ObserveAllowance(now.AddMinutes(1), 100, reset, accountEmail: "second@example.com"),
                ObserveAllowance(now.AddMinutes(2), 99, reset, accountEmail: "second@example.com"),
                ObserveAllowance(now.AddMinutes(3), 100, reset, accountEmail: "second@example.com")),
            new ActivationRecordingCommand(), settings, new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var switched = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var usedUp = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Empty(switched.Notices);
        Assert.Equal("5-hour allowance used up.", Assert.Single(usedUp.Notices).Message);
    }

    [Fact]
    public async Task AccountSwitchDiscardsPendingActivation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var newReset = originalReset.AddMinutes(1);
        var settings = new ActivationSettings();
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, originalReset, accountEmail: "first@example.com"),
                ObserveAllowance(now.AddMinutes(1), 0, newReset, accountEmail: "second@example.com")),
            command, settings, time);

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(2, command.CallCount);
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
    }

    [Fact]
    public async Task AccountSwitchDuringActivationRecoveryDiscardsOldTarget()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var originalReset = now.AddHours(5);
        var newReset = originalReset.AddMinutes(1);
        var settings = new ActivationSettings();
        var command = new ActivationRecordingCommand();
        command.FailNext(new IOException("Activation failed."));
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, originalReset, accountEmail: "first@example.com"),
                ObserveAllowance(now.AddSeconds(1), 0, newReset, accountEmail: "second@example.com"),
                ObserveAllowance(now.AddMinutes(1), 0, newReset, accountEmail: "second@example.com")),
            command, settings, new ActivationTimeProvider(now));

        var recovered = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Empty(recovered.Notices);
        Assert.Equal(2, command.CallCount);
        Assert.Null(settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
    }

    [Fact]
    public async Task ConcurrentUpdatesShareOneActivationCommand()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var command = new BlockingActivationCommand();
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, reset),
                ObserveAllowance(now, 0, reset)),
            command,
            new ActivationSettings(),
            new ActivationTimeProvider(now));

        var first = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        await command.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
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

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(2, command.CallCount);
        Assert.Equal(fiveHourReset.AddMinutes(1), settings.ReadActivatedReset(AllowanceWindowKind.FiveHour));
        Assert.Equal(weeklyReset.AddMinutes(2), settings.ReadActivatedReset(AllowanceWindowKind.Weekly));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public async Task ActivationDoesNotSendRequestWhenEitherWindowIsUsedUp(
        int fiveHourUsedPercent,
        int weeklyUsedPercent)
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var command = new ActivationRecordingCommand();
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(
                    now,
                    fiveHourUsedPercent,
                    now.AddHours(5),
                    weeklyUsedPercent,
                    now.AddDays(7))),
            command,
            new ActivationSettings(),
            new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(0, command.CallCount);
    }

    [Fact]
    public async Task ActivationWaitsToRetryUntilBothWindowsHaveAllowance()
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var fiveHourReset = now.AddHours(5);
        var weeklyReset = now.AddDays(7);
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, fiveHourReset, 50, weeklyReset),
                ObserveAllowance(now.AddMinutes(1), 0, fiveHourReset, 100, weeklyReset),
                ObserveAllowance(now.AddMinutes(2), 0, fiveHourReset, 99, weeklyReset)),
            command,
            new ActivationSettings(),
            time);

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(1, command.CallCount);

        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(1, command.CallCount);

        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(2, command.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandFailureBacksOffForFiveMinutesWithoutConsumingTheRetryBudget(bool recoveryFails)
    {
        var now = new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.FromHours(2));
        var reset = now.AddHours(5);
        var reader = new ActivationObservationReader();
        reader.Enqueue(ObserveAllowance(now, 0, reset));
        if (recoveryFails)
        {
            reader.EnqueueFailure(new IOException("synthetic observation failure"));
        }
        else
        {
            reader.Enqueue(ObserveAllowance(now.AddSeconds(1), 0, reset));
        }
        reader.Enqueue(ObserveAllowance(now.AddMinutes(4), 0, reset));
        for (var minute = 5; minute <= 9; minute++)
        {
            reader.Enqueue(ObserveAllowance(now.AddMinutes(minute), 0, reset));
        }
        var command = new ActivationRecordingCommand();
        command.FailNext(new InvalidOperationException("synthetic command failure"));
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(reader, command, new ActivationSettings(), time);

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(1, command.CallCount);
        time.Advance(TimeSpan.FromMinutes(4));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(1, command.CallCount);

        // The failed command leaves the initial successful attempt and all three retries available.
        foreach (var expectedCalls in new[] { 2, 3, 4, 5, 5 })
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
            Assert.Equal(expectedCalls, command.CallCount);
        }
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

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task PendingActivationStopsWhenAllowanceIsNoLongerReported()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(2));
        var command = new ActivationRecordingCommand();
        var time = new ActivationTimeProvider(now);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, now.AddHours(5)),
                ObserveWeeklyAllowance(now.AddMinutes(1), 40, now.AddDays(4))),
            command,
            new ActivationSettings(),
            time);

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        command.FailNext(new InvalidOperationException("synthetic retry failure"));
        time.Advance(TimeSpan.FromMinutes(1));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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
            await first.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        }

        var restartedCommand = new ActivationRecordingCommand();
        await using var restarted = CreateActivationUpdates(
            new ActivationObservationReader(ObserveAllowance(now, 0, reset)),
            restartedCommand,
            settings,
            new ActivationTimeProvider(now));
        await restarted.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

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

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var usedUp = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var reset = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal("5-hour and weekly allowance used up.", Assert.Single(usedUp.Notices).Message);
        Assert.Equal("5-hour and weekly allowance reset.", Assert.Single(reset.Notices).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentationHistoryFollowsTheSelectedAllowanceAcrossUpdates(bool sameResetTime)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var fiveHourReset = now.AddHours(4);
        var weeklyReset = sameResetTime ? fiveHourReset : now.AddDays(6);
        var weeklyActivatedReset = weeklyReset.AddMinutes(-1);
        var settings = new ActivationSettings { ActivationEnabled = false };
        settings.WriteActivatedReset(AllowanceWindowKind.FiveHour, fiveHourReset);
        settings.WriteActivatedReset(AllowanceWindowKind.Weekly, weeklyActivatedReset);
        var both = ObserveAllowance(now, 20, fiveHourReset, 30, weeklyReset);
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(both, ObserveWeeklyAllowance(now, 30, weeklyReset), both),
            new ActivationRecordingCommand(), settings, new ActivationTimeProvider(now));

        var fiveHour = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var weekly = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var returned = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(AllowanceWindowKind.FiveHour, fiveHour.Popup.ActivityIndicator.WindowKind);
        Assert.Equal(fiveHourReset, fiveHour.Popup.ActivityIndicator.ActivatedResetAt);
        Assert.Equal(AllowanceWindowKind.Weekly, weekly.Popup.ActivityIndicator.WindowKind);
        Assert.Equal(weeklyActivatedReset, weekly.Popup.ActivityIndicator.ActivatedResetAt);
        Assert.Equal(fiveHour.Popup.ActivityIndicator, returned.Popup.ActivityIndicator);
        Assert.Equal([AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly, AllowanceWindowKind.FiveHour],
            settings.HistoryReads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PresentationOnlyDependsOnSelectedAllowanceHistory(bool weeklyOnly, bool selectedReadFails)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var selected = weeklyOnly ? AllowanceWindowKind.Weekly : AllowanceWindowKind.FiveHour;
        var unselected = weeklyOnly ? AllowanceWindowKind.FiveHour : AllowanceWindowKind.Weekly;
        var settings = new ActivationSettings
        {
            ActivationEnabled = false,
            DeniedHistory = selectedReadFails ? selected : unselected
        };
        var observation = weeklyOnly
            ? ObserveWeeklyAllowance(now, 30, now.AddDays(6))
            : ObserveAllowance(now, 20, now.AddHours(4), 30, now.AddDays(6));
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(observation), new ActivationRecordingCommand(),
            settings, new ActivationTimeProvider(now));

        if (selectedReadFails)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken));
        }
        else
        {
            var presentation = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
            Assert.Equal(selected, presentation.Popup.ActivityIndicator.WindowKind);
            Assert.Null(presentation.Popup.ActivityIndicator.ActivatedResetAt);
        }
        Assert.Equal(selected, Assert.Single(settings.HistoryReads));
    }

    [Fact]
    public async Task MissingAllowancesDoNotReadActivationHistory()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var observation = new UsageObservations(
            new AccountUsageObservation(now, [], "plus", "Codex", new AccountActivityObservation.NotRequested()), null);
        var settings = new ActivationSettings
        {
            ActivationEnabled = false,
            DeniedHistory = AllowanceWindowKind.Weekly
        };
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(observation), new ActivationRecordingCommand(),
            settings, new ActivationTimeProvider(now));

        var presentation = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(UsagePresentation.ActivityIndicatorPresentation.Unavailable, presentation.Popup.ActivityIndicator);
        Assert.Empty(settings.HistoryReads);
    }

    [Fact]
    public async Task PresentationReadsHistoryForTheAllowanceSelectedAfterActivationRecovery()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var weeklyReset = now.AddDays(6);
        var settings = new ActivationSettings();
        settings.WriteActivatedReset(AllowanceWindowKind.Weekly, weeklyReset);
        var command = new ActivationRecordingCommand();
        command.FailNext(new InvalidOperationException("Synthetic activation failure."));
        await using var updates = CreateActivationUpdates(
            new ActivationObservationReader(
                ObserveAllowance(now, 0, now.AddHours(5), 30, weeklyReset),
                ObserveWeeklyAllowance(now, 30, weeklyReset)),
            command, settings, new ActivationTimeProvider(now));

        var presentation = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.Equal(1, command.CallCount);
        Assert.Equal(AllowanceWindowKind.Weekly, presentation.Popup.ActivityIndicator.WindowKind);
        Assert.Equal(weeklyReset, presentation.Popup.ActivityIndicator.ActivatedResetAt);
        Assert.Equal([AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly], settings.HistoryReads);
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
        DateTimeOffset? weeklyReset = null,
        string? accountEmail = null) =>
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
                new AccountActivityObservation.NotRequested(),
                accountEmail),
            Local: null);

    private static UsageObservations ObserveWeeklyAllowance(
        DateTimeOffset observedAt,
        int usedPercent,
        DateTimeOffset? reset) =>
        new(
            new AccountUsageObservation(
                observedAt,
                [new AllowanceWindow(usedPercent, TimeSpan.FromDays(7), reset)],
                "pro",
                "Codex",
                new AccountActivityObservation.NotRequested()),
            Local: null);

    private sealed class ActivationObservationReader : IUsageObservationReader
    {
        private readonly Queue<Func<CancellationToken, Task<UsageObservations>>> reads = [];

        public ActivationObservationReader(params UsageObservations[] observations)
        {
            foreach (var observation in observations)
            {
                Enqueue(observation);
            }
        }

        public int CallCount { get; private set; }

        public void Enqueue(UsageObservations observation) => EnqueueRead(_ => Task.FromResult(observation));

        public void EnqueueRead(Func<CancellationToken, Task<UsageObservations>> read) => reads.Enqueue(read);

        public void EnqueueFailure(Exception exception) => reads.Enqueue(
            _ => Task.FromException<UsageObservations>(exception));

        public Task<UsageObservations> ReadAsync(
            UsageObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(UsageObservationRequest.AllowanceWindows, request);
            CallCount++;
            return reads.Dequeue()(cancellationToken);
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

    private sealed class ActivationSettings : IAllowanceWindowActivationSettings
    {
        private readonly Dictionary<AllowanceWindowKind, DateTimeOffset> activatedResets = [];

        public bool ActivationEnabled { get; set; } = true;
        public bool NotificationsEnabled { get; set; }
        public AllowanceWindowKind? DeniedHistory { get; init; }
        public List<AllowanceWindowKind> HistoryReads { get; } = [];

        public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window)
        {
            HistoryReads.Add(window);
            if (DeniedHistory == window)
            {
                throw new UnauthorizedAccessException("Synthetic activation history read failure.");
            }
            return activatedResets.TryGetValue(window, out var reset) ? reset : null;
        }

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
