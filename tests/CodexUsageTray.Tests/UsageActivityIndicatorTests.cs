namespace CodexUsageTray.Tests;

public sealed class UsageActivityIndicatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UsedUpWeeklyAllowanceIsVisibleWhenFiveHourAllowanceHasCapacity(bool hasFiveHour) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var snapshot = new UsageSnapshot(
                now, now,
                hasFiveHour ? new AllowanceWindow(20, TimeSpan.FromHours(5), now.AddHours(3)) : null,
                new AllowanceWindow(100, TimeSpan.FromDays(7), now.AddDays(2)),
                lifetimeTokens: null, todayTokens: null, plan: "plus", limitName: "Codex");
            var presentation = UsagePresentation.Create(snapshot, now, System.Globalization.CultureInfo.InvariantCulture);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(presentation.Popup.ActivityIndicator);

            Assert.Equal(AllowanceFlashPhase.UsedUp, indicator.Flash(now));
        });

    [Theory]
    [InlineData(20, 100, 10, 2880, "Weekly", "UsedUp")]
    [InlineData(100, 100, 10, 2880, "Weekly", "UsedUp")]
    [InlineData(100, 100, 120, 10, "FiveHour", "UsedUp")]
    [InlineData(100, 100, 10, 15, "Weekly", "BeforeReset")]
    [InlineData(100, 100, 15, 10, "FiveHour", "BeforeReset")]
    [InlineData(0, 100, 300, 2880, "Weekly", "UsedUp")]
    [InlineData(100, 0, 120, 10080, "FiveHour", "UsedUp")]
    [InlineData(100, 100, 10, null, "Weekly", "UsedUp")]
    [InlineData(100, 100, null, 10, "FiveHour", "UsedUp")]
    [InlineData(100, 100, 10, 10, "FiveHour", "BeforeReset")]
    [InlineData(100, 100, null, null, "FiveHour", "UsedUp")]
    [InlineData(50, 50, 150, 5040, "FiveHour", "Normal")]
    public Task RingShowsTheAllowanceThatStillBlocksUse(
        int fiveHourUsed, int weeklyUsed, int? fiveHourResetMinutes, int? weeklyResetMinutes,
        string expectedWindow, string expectedFlash) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var snapshot = new UsageSnapshot(
                now, now,
                new AllowanceWindow(fiveHourUsed, TimeSpan.FromHours(5),
                    fiveHourResetMinutes is { } fiveHourMinutes ? now.AddMinutes(fiveHourMinutes) : null),
                new AllowanceWindow(weeklyUsed, TimeSpan.FromDays(7),
                    weeklyResetMinutes is { } weeklyMinutes ? now.AddMinutes(weeklyMinutes) : null),
                lifetimeTokens: null, todayTokens: null, plan: "plus", limitName: "Codex");
            var presentation = UsagePresentation.Create(snapshot, now, System.Globalization.CultureInfo.InvariantCulture);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(presentation.Popup.ActivityIndicator);

            Assert.Equal(expectedWindow, presentation.Popup.ActivityIndicator.WindowKind.ToString());
            Assert.Equal(expectedFlash, indicator.Flash(now).ToString());
        });

    [Fact]
    public Task RingUsesTheMoreConsumedAllowancesPercentageAndPace() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var snapshot = new UsageSnapshot(
                now, now,
                new AllowanceWindow(0, TimeSpan.FromHours(5), now.AddHours(5)),
                new AllowanceWindow(50, TimeSpan.FromDays(7), now.AddDays(3.5)),
                lifetimeTokens: null, todayTokens: null, plan: "plus", limitName: "Codex");
            var presentation = UsagePresentation.Create(snapshot, now, System.Globalization.CultureInfo.InvariantCulture);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(presentation.Popup.ActivityIndicator);

            Assert.Equal(180, indicator.RingSweep);
            Assert.Equal(6, indicator.RingDegreesPerSecond(now));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
            indicator.Advance(now);
            indicator.Advance(now.AddMilliseconds(50));
            Assert.InRange(indicator.RingAngle, .29f, .31f);
        });

    [Fact]
    public Task CombinesRecentActivityWithAllowancePaceAndFlashPhase() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowSession(new CodexSessionActivity(now, CodexModel.Sol, "6"));
            indicator.Advance(now);
            Assert.Contains("GPT-6 Sol", indicator.AccessibleDescription);
            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 20,
                ResetsAt: now.AddHours(4.5),
                WindowDuration: TimeSpan.FromHours(5),
                AwaitingActivation: false));

            Assert.True(indicator.IsActive);
            Assert.Equal(UsagePace.High, indicator.Pace(now));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
        });

    [Theory]
    [InlineData(20, 901, "Normal")]
    [InlineData(20, 900, "BeforeReset")]
    [InlineData(20, 30, "BeforeReset")]
    [InlineData(20, 0, "Normal")]
    [InlineData(20, -1, "Normal")]
    [InlineData(0, 0, "UsedUp")]
    [InlineData(0, -120, "UsedUp")]
    public Task FlashReflectsTheTimeUntilReset(int remaining, int secondsUntilReset, string expected) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, remaining, now.AddSeconds(secondsUntilReset), TimeSpan.FromHours(5)));

            Assert.Equal(expected, indicator.Flash(now).ToString());
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RepeatedPresentationsDoNotExtendThePostActivationFlash(bool weekly) =>
        StaTest.RunAsync(() =>
        {
            var activatedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var duration = weekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);
            var allowance = UsagePresentation.ActivityIndicatorPresentation.Create(
                weekly ? AllowanceWindowKind.Weekly : AllowanceWindowKind.FiveHour,
                new AllowanceWindow(1, duration, activatedAt.Add(duration)));
            using var indicator = new UsageActivityIndicator();

            for (var minutes = 0; minutes <= 10; minutes++)
            {
                indicator.ShowAllowance(allowance);
                Assert.Equal(minutes <= 5 ? AllowanceFlashPhase.AfterReset : AllowanceFlashPhase.Normal,
                    indicator.Flash(activatedAt.AddMinutes(minutes)));
            }
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddTicks(1)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddDays(1)));
        });

    [Fact]
    public Task ResetFlashStopsAfterFiveMinutesEvenWithoutActivation() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            var waiting = new UsagePresentation.ActivityIndicatorPresentation(
                AllowanceWindowKind.FiveHour, 100, now.AddHours(5), TimeSpan.FromHours(5),
                AwaitingActivation: true, ResetFlashStartedAt: now);
            indicator.ShowAllowance(waiting);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now.AddMinutes(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now.AddMinutes(5).AddTicks(1)));

            var activated = waiting with
            {
                ResetsAt = now.AddMinutes(4).AddHours(5),
                AwaitingActivation = false,
                RemainingPercent = 99
            };
            indicator.ShowAllowance(activated);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now.AddMinutes(5).AddTicks(1)));

            using var restarted = new UsageActivityIndicator();
            restarted.ShowAllowance(activated);
            Assert.Equal(AllowanceFlashPhase.Normal, restarted.Flash(now.AddMinutes(10)));
        });

    [Theory]
    [InlineData(180, "Normal")]
    [InlineData(298, "AfterReset")]
    public Task FullAllowanceOnStartupReflectsTheWindowAge(int minutesUntilReset, string expected) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(UsagePresentation.ActivityIndicatorPresentation.Create(
                AllowanceWindowKind.FiveHour,
                new AllowanceWindow(0, TimeSpan.FromHours(5), now.AddMinutes(minutesUntilReset))));

            Assert.Equal(expected, indicator.Flash(now).ToString());
        });

    [Fact]
    public Task PresentationDeterminesTheBoundedResetFlashWithoutEarlierFrames() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var waiting = new UsagePresentation.ActivityIndicatorPresentation(
                AllowanceWindowKind.FiveHour, 100, now.AddHours(3), TimeSpan.FromHours(5),
                AwaitingActivation: true, ResetFlashStartedAt: now);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(waiting);
            indicator.Advance(now);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
            Assert.Contains("ready to activate", indicator.AccessibleDescription);

            indicator.ShowAllowance(waiting with { AwaitingActivation = false });
            indicator.Advance(now);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
            Assert.DoesNotContain("ready to activate", indicator.AccessibleDescription);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now.AddMinutes(5).AddTicks(1)));
        });

    [Fact]
    public Task UsedUpFlashYieldsToTheFinalFifteenMinuteResetFlash() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 0, now.AddHours(2), TimeSpan.FromHours(5)));
            Assert.Equal(AllowanceFlashPhase.UsedUp, indicator.Flash(now));

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 0, null, TimeSpan.FromHours(5)));
            Assert.Equal(AllowanceFlashPhase.UsedUp, indicator.Flash(now));

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 0, now.AddMinutes(15), TimeSpan.FromHours(5)));
            Assert.Equal(AllowanceFlashPhase.BeforeReset, indicator.Flash(now));

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now.AddHours(5), TimeSpan.FromHours(5),
                AwaitingActivation: true, ResetFlashStartedAt: now));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
        });

    [Fact]
    public Task RingRotationFollowsAllowanceUseEvenWhenIdle() =>
        StaTest.RunAsync(() =>
        {
            var now = DateTimeOffset.Now;
            using var indicator = new UsageActivityIndicator();
            var resetsAt = now.AddHours(2.5);
            void ShowRemaining(int remaining) => indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, remaining, resetsAt, TimeSpan.FromHours(5)));

            ShowRemaining(100);
            Assert.Equal(0, indicator.RingDegreesPerSecond(now));
            indicator.Advance(now);
            indicator.Advance(now.AddMilliseconds(50));
            Assert.Equal(0, indicator.RingAngle);

            ShowRemaining(75);
            Assert.InRange(indicator.RingDegreesPerSecond(now), 2.99f, 3.01f);
            ShowRemaining(50);
            Assert.InRange(indicator.RingDegreesPerSecond(now), 5.99f, 6.01f);
            ShowRemaining(25);
            Assert.InRange(indicator.RingDegreesPerSecond(now), 8.99f, 9.01f);
            indicator.Advance(now.AddMilliseconds(100));
            Assert.InRange(indicator.RingAngle, .44f, .46f);
            Assert.Equal(0, indicator.IndicatorStrength(now));
        });

    [Fact]
    public Task IndicatorSlowsAndFadesOverFifteenSeconds() =>
        StaTest.RunAsync(() =>
        {
            var now = DateTimeOffset.Now;
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 50, now.AddHours(2.5), TimeSpan.FromHours(5)));
            indicator.ShowSession(new CodexSessionActivity(now, CodexModel.Terra));

            indicator.Advance(now);
            indicator.Advance(now.AddMilliseconds(50));
            Assert.InRange(indicator.RingAngle, .29f, .31f);
            Assert.InRange(indicator.IndicatorAngle, 1.79f, 1.81f);
            Assert.InRange(indicator.IndicatorStrength(now.AddSeconds(7.5)), .49f, .51f);

            indicator.Advance(now.AddSeconds(7.5));
            var fadedAngle = indicator.IndicatorAngle;
            var fadedRingAngle = indicator.RingAngle;
            Assert.False(indicator.IsActive);
            Assert.InRange(fadedAngle, 6.29f, 6.31f);

            indicator.Advance(now.AddSeconds(15));
            Assert.Equal(0, indicator.IndicatorStrength(now.AddSeconds(15)));
            Assert.Equal(fadedAngle, indicator.IndicatorAngle);
            Assert.True(indicator.RingAngle > fadedRingAngle);
        });

    [Fact]
    public Task KeepsAVisibleMarkerAtZeroAllowance() =>
        StaTest.RunAsync(() =>
        {
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 0,
                ResetsAt: DateTimeOffset.Now.AddHours(5),
                WindowDuration: TimeSpan.FromHours(5),
                AwaitingActivation: false));

            Assert.Equal(4, indicator.RingSweep);
        });
}
