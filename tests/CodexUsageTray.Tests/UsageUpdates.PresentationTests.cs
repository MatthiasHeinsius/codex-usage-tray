namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
    [Fact]
    public Task SelectingAnAllowanceAgainDoesNotRestartItsExpiredCyanFlash() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(now);
            var weeklyReset = now.AddMinutes(7);
            var activatedReset = now.AddHours(5).AddMinutes(4);
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                ObserveAllowance(now, 40, now, 0, weeklyReset),
                ObserveAllowance(now, 0, now.AddHours(5), 0, weeklyReset),
                ObserveAllowance(now.AddMinutes(4), 1, activatedReset, 0, weeklyReset),
                ObserveAllowance(now.AddMinutes(6), 1, activatedReset, 2, weeklyReset),
                ObserveAllowance(now.AddMinutes(7), 1, activatedReset, 0, weeklyReset.AddDays(7))),
                new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false }, time);
            await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            using var indicator = new UsageActivityIndicator();

            foreach (var (minutes, kind, phase) in new[]
                {
                    (0, AllowanceWindowKind.FiveHour, AllowanceFlashPhase.AfterReset),
                    (4, AllowanceWindowKind.FiveHour, AllowanceFlashPhase.AfterReset),
                    (2, AllowanceWindowKind.Weekly, AllowanceFlashPhase.BeforeReset),
                    (1, AllowanceWindowKind.FiveHour, AllowanceFlashPhase.Normal)
                })
            {
                time.Advance(TimeSpan.FromMinutes(minutes));
                var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
                Assert.Equal(kind, ready.Popup.ActivityIndicator.WindowKind);
                indicator.ShowAllowance(ready.Popup.ActivityIndicator);
                Assert.Equal(phase, indicator.Flash(time.GetUtcNow()));
            }
        });

    [Fact]
    public Task UnknownAccountDoesNotShowCyanWithoutReliableFlashHistory() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(now);
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                ObserveAllowance(now, 0, now.AddHours(5), accountEmail: null),
                ObserveAllowance(now.AddMinutes(4), 1, now.AddHours(5).AddMinutes(4), accountEmail: null)),
                new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false }, time);
            using var indicator = new UsageActivityIndicator();

            for (var refresh = 0; refresh < 2; refresh++)
            {
                var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
                indicator.ShowAllowance(ready.Popup.ActivityIndicator);
                Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(time.GetUtcNow()));
                time.Advance(TimeSpan.FromMinutes(4));
            }
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task CyanFlashExpiresEvenWhenWaitingOrActivationMovesTheReset(bool activates, bool weekly) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(now);
            var reset = now.Add(weekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5));
            UsageObservations Observe(DateTimeOffset at, int used, DateTimeOffset resetsAt) => weekly
                ? ObserveWeeklyAllowance(at, used, resetsAt)
                : ObserveAllowance(at, used, resetsAt);
            var renewed = Observe(now, 0, reset);
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                Observe(now, 40, now), renewed,
                activates ? Observe(now.AddMinutes(4), 1, reset.AddMinutes(4)) : renewed),
                new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false }, time);
            using var indicator = new UsageActivityIndicator();

            await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            indicator.ShowAllowance(ready.Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));

            time.Advance(TimeSpan.FromMinutes(4));
            ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            indicator.ShowAllowance(ready.Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(time.GetUtcNow()));

            foreach (var presentation in new UsagePresentation[]
                {
                    ready,
                    UsagePresentation.CreateLoading(ready),
                    UsagePresentation.CreateFailed(ready, "Offline")
                })
            {
                indicator.ShowAllowance(presentation.Popup.ActivityIndicator);
                Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now.AddMinutes(5).AddTicks(1)));
                Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now.AddDays(1)));
            }
        });

    [Theory]
    [InlineData(4, "AfterReset")]
    [InlineData(6, "Normal")]
    [InlineData(20, "Normal")]
    public Task LateResetObservationsUseTheOriginalResetTime(int delayMinutes, string expected) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var resetAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(resetAt.AddMinutes(-1));
            var observedAt = resetAt.AddMinutes(delayMinutes);
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                ObserveAllowance(time.GetUtcNow(), 40, resetAt),
                ObserveAllowance(observedAt, 0, observedAt.AddHours(5))),
                new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false }, time);
            await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            time.Advance(TimeSpan.FromMinutes(delayMinutes + 1));
            var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(ready.Popup.ActivityIndicator);

            Assert.Equal(expected, indicator.Flash(observedAt).ToString());
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(resetAt.AddMinutes(5).AddTicks(1)));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MovingResetTimesAndUsageCorrectionsDoNotRestartTheCyanFlash(bool usageCorrected) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(now);
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                ObserveAllowance(now, 0, now.AddHours(5)),
                ObserveAllowance(now.AddMinutes(4), usageCorrected ? 1 : 0, now.AddHours(5).AddMinutes(4)),
                ObserveAllowance(now.AddMinutes(8), 0, now.AddHours(5).AddMinutes(8))),
                new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false }, time);
            using var indicator = new UsageActivityIndicator();

            for (var refresh = 0; refresh < 3; refresh++)
            {
                var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
                indicator.ShowAllowance(ready.Popup.ActivityIndicator);
                Assert.Equal(refresh < 2 ? AllowanceFlashPhase.AfterReset : AllowanceFlashPhase.Normal,
                    indicator.Flash(time.GetUtcNow()));
                time.Advance(TimeSpan.FromMinutes(4));
            }
        });

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public Task ANaturalResetStartsANewBoundedCyanFlash(bool activationConfirmed, int usedAfterReset) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(now);
            var settings = new ActivationSettings { ActivationEnabled = false };
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                ObserveAllowance(now, 40, now),
                ObserveAllowance(now, 0, now.AddHours(5)),
                ObserveAllowance(now.AddHours(5), usedAfterReset, now.AddHours(10))),
                new ActivationRecordingCommand(), settings, time);
            await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            time.Advance(TimeSpan.FromHours(5));
            if (activationConfirmed)
            {
                settings.WriteActivatedReset(AllowanceWindowKind.FiveHour, now.AddHours(10));
            }
            var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(ready.Popup.ActivityIndicator);

            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(time.GetUtcNow()));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(time.GetUtcNow().AddMinutes(5).AddTicks(1)));
        });

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public Task FiveHourResetDoesNotFlashWhileWeeklyAllowanceBlocksUse(int fiveHourUsed) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new ActivationTimeProvider(now);
            var fiveHourReset = now.AddMinutes(10);
            var weeklyReset = now.AddDays(1);
            await using var updates = CreateActivationUpdates(new ActivationObservationReader(
                ObserveAllowance(now, fiveHourUsed, fiveHourReset, 100, weeklyReset),
                ObserveAllowance(fiveHourReset, 0, fiveHourReset.AddHours(5), 100, weeklyReset)),
                new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false }, time);
            using var indicator = new UsageActivityIndicator();

            for (var refresh = 0; refresh < 2; refresh++)
            {
                var ready = await updates.RequestAsync(UsageUpdateIntent.Routine, cancellationToken);
                foreach (var presentation in new UsagePresentation[]
                    {
                        ready,
                        UsagePresentation.CreateLoading(ready),
                        UsagePresentation.CreateFailed(ready, "Offline")
                    })
                {
                    Assert.Equal(AllowanceWindowKind.Weekly, presentation.Popup.ActivityIndicator.WindowKind);
                    indicator.ShowAllowance(presentation.Popup.ActivityIndicator);
                    Assert.Equal(AllowanceFlashPhase.UsedUp, indicator.Flash(time.GetUtcNow()));
                }
                time.Advance(TimeSpan.FromMinutes(10));
            }
        });

    [Fact]
    public async Task CriticalAllowanceIsReevaluatedAfterEachRefresh()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var fiveHourReset = now.AddMinutes(10);
        var weeklyReset = now.AddDays(2);
        var settings = new ActivationSettings { ActivationEnabled = false };
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            ObserveAllowance(now, 90, fiveHourReset, 50, weeklyReset),
            ObserveAllowance(now, 90, fiveHourReset, 100, weeklyReset),
            ObserveAllowance(now, 0, now.AddHours(5), 100, weeklyReset),
            ObserveAllowance(now, 60, now.AddHours(5), 20, weeklyReset)),
            new ActivationRecordingCommand(), settings,
            new ActivationTimeProvider(now));

        foreach (var (kind, remaining) in new[]
            {
                (AllowanceWindowKind.FiveHour, 10),
                (AllowanceWindowKind.Weekly, 0),
                (AllowanceWindowKind.Weekly, 0),
                (AllowanceWindowKind.FiveHour, 40)
            })
        {
            var presentation = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
            Assert.Equal(kind, presentation.Popup.ActivityIndicator.WindowKind);
            Assert.Equal(remaining, presentation.Popup.ActivityIndicator.RemainingPercent);
            Assert.False(presentation.Popup.ActivityIndicator.AwaitingActivation);
        }
        Assert.Equal(
            [AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly, AllowanceWindowKind.Weekly, AllowanceWindowKind.FiveHour],
            settings.HistoryReads);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(40, false)]
    [InlineData(0, false)]
    public async Task VisualResetWaitsForActivationWhileNotificationsRequireUsedUpAllowance(
        int initialUsedPercent, bool expectsNotice)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(5);
        var settings = new ActivationSettings { ActivationEnabled = false, NotificationsEnabled = true };
        var command = new ActivationRecordingCommand();
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            ObserveAllowance(now, initialUsedPercent, now),
            ObserveAllowance(now, 0, reset),
            ObserveAllowance(now.AddMinutes(20), 1, reset),
            ObserveAllowance(now.AddMinutes(20), 1, reset.AddMinutes(20))),
            command, settings, new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var renewed = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.True(renewed.Popup.ActivityIndicator.AwaitingActivation);
        Assert.Equal(expectsNotice ? 1 : 0, renewed.Notices.Length);
        var used = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.True(used.Popup.ActivityIndicator.AwaitingActivation);
        var activated = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.False(activated.Popup.ActivityIndicator.AwaitingActivation);
        Assert.Equal(0, command.CallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ChangingOrRemovingTheSelectedAllowanceDiscardsWaitingHistory(bool fiveHour, bool missing)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(3);
        UsageObservations Observe(bool selectFiveHour, int used, DateTimeOffset resetsAt) => selectFiveHour
            ? ObserveAllowance(now, used, resetsAt)
            : ObserveWeeklyAllowance(now, used, resetsAt);
        var renewed = Observe(fiveHour, 0, reset);
        var changed = missing
            ? renewed with { Account = renewed.Account with { AllowanceWindows = [] } }
            : Observe(!fiveHour, 0, reset);
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            Observe(fiveHour, 100, now), renewed, changed, renewed),
            new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false },
            new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.True((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
        Assert.False((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
        Assert.False((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
    }

    [Theory]
    [InlineData("other@example.com")]
    [InlineData(null)]
    public async Task NewOrUnavailableAccountDoesNotLookLikeAVisualReset(string? accountEmail)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            ObserveAllowance(now, 40, now.AddHours(2)),
            ObserveAllowance(now, 0, now.AddHours(3), accountEmail: accountEmail)),
            new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false },
            new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var changed = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);

        Assert.False(changed.Popup.ActivityIndicator.AwaitingActivation);
        Assert.Empty(changed.Notices);
    }

    [Theory]
    [InlineData("other@example.com", false)]
    [InlineData(null, false)]
    [InlineData("USER@example.com", true)]
    public async Task WaitingHistoryBelongsToTheSameKnownAccount(string? accountEmail, bool keepsWaiting)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(3);
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            ObserveAllowance(now, 100, now), ObserveAllowance(now, 0, reset),
            ObserveAllowance(now, 0, reset, accountEmail: accountEmail)),
            new ActivationRecordingCommand(), new ActivationSettings { ActivationEnabled = false },
            new ActivationTimeProvider(now));

        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.True((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
        var changed = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(keepsWaiting, changed.Popup.ActivityIndicator.AwaitingActivation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledPresentationDoesNotCommitVisualHistory(bool cancel)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var renewed = ObserveAllowance(now, 0, now.AddHours(3));
        var settings = new ActivationSettings { ActivationEnabled = false };
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            ObserveAllowance(now, 100, now), renewed,
            ObserveAllowance(now, 0, now.AddHours(4), accountEmail: "other@example.com"), renewed),
            new ActivationRecordingCommand(), settings, new ActivationTimeProvider(now));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        var waiting = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.True(waiting.Popup.ActivityIndicator.AwaitingActivation);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancel)
        {
            settings.BeforeHistoryRead = cancellation.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                updates.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token));
            settings.BeforeHistoryRead = null;
        }
        else
        {
            settings.DeniedHistory = AllowanceWindowKind.FiveHour;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                updates.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token));
            settings.DeniedHistory = null;
        }

        var next = await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.Equal(waiting.Popup.ActivityIndicator, next.Popup.ActivityIndicator);
        Assert.Equal(waiting.Popup.ActivityIndicator, UsagePresentation.CreateLoading(waiting).Popup.ActivityIndicator);
        Assert.Equal(waiting.Popup.ActivityIndicator,
            UsagePresentation.CreateFailed(waiting, "Offline").Popup.ActivityIndicator);
    }

    [Fact]
    public async Task ConfirmedMarkerClearsWaitingEvenWhenAutomaticActivationIsDisabled()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(3);
        var renewed = ObserveAllowance(now, 0, reset);
        var settings = new ActivationSettings { ActivationEnabled = false };
        await using var updates = CreateActivationUpdates(new ActivationObservationReader(
            ObserveAllowance(now, 100, now), renewed, renewed, renewed),
            new ActivationRecordingCommand(), settings, new ActivationTimeProvider(now));
        await updates.RequestAsync(UsageUpdateIntent.Routine, TestContext.Current.CancellationToken);
        Assert.True((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
        settings.WriteActivatedReset(AllowanceWindowKind.FiveHour, reset);
        Assert.False((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
        Assert.False((await updates.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);

        await using var restarted = CreateActivationUpdates(new ActivationObservationReader(renewed),
            new ActivationRecordingCommand(), settings, new ActivationTimeProvider(now));
        Assert.False((await restarted.RequestAsync(UsageUpdateIntent.Routine,
            TestContext.Current.CancellationToken)).Popup.ActivityIndicator.AwaitingActivation);
    }
}
