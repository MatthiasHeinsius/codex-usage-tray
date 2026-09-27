namespace CodexUsageTray.Tests;

public sealed partial class UsageUpdatesTests
{
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
