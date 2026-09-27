namespace CodexUsageTray.Tests;

public sealed class UsageActivityIndicatorTests
{
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

    [Fact]
    public Task ResetFlashLastsUntilFiveMinutesAfterActivation() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, now.AddMinutes(-10), TimeSpan.FromHours(5), AwaitingActivation: true));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));

            var activatedAt = now.AddMinutes(10);
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, activatedAt.AddHours(5), TimeSpan.FromHours(5)));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(activatedAt.AddMinutes(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));

            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 99, activatedAt.AddHours(5), TimeSpan.FromHours(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));

            using var restarted = new UsageActivityIndicator();
            restarted.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, activatedAt.AddHours(5), TimeSpan.FromHours(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, restarted.Flash(activatedAt.AddMinutes(10)));
        });

    [Theory]
    [InlineData(180, "Normal")]
    [InlineData(298, "AfterReset")]
    public Task FullAllowanceOnStartupReflectsTheWindowAge(int minutesUntilReset, string expected) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, now.AddMinutes(minutesUntilReset), TimeSpan.FromHours(5)));

            Assert.Equal(expected, indicator.Flash(now).ToString());
        });

    [Fact]
    public Task PresentationDeterminesWaitingWithoutEarlierFrames() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var waiting = new UsagePresentation.ActivityIndicatorPresentation(
                AllowanceWindowKind.FiveHour, 100, now.AddHours(3), TimeSpan.FromHours(5), AwaitingActivation: true);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(waiting);
            indicator.Advance(now);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
            Assert.Contains("ready to activate", indicator.AccessibleDescription);

            indicator.ShowAllowance(waiting with { AwaitingActivation = false });
            indicator.Advance(now);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
            Assert.DoesNotContain("ready to activate", indicator.AccessibleDescription);
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

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now.AddHours(5), TimeSpan.FromHours(5), AwaitingActivation: true));
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
