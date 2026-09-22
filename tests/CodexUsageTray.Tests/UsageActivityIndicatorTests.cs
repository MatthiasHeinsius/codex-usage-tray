namespace CodexUsageTray.Tests;

public sealed class UsageActivityIndicatorTests
{
    [Fact]
    public Task CombinesRecentActivityWithAllowancePaceAndResetPhase() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowSession(new CodexSessionActivity(DateTimeOffset.Now, CodexModel.Sol, "6"));
            indicator.Advance(DateTimeOffset.Now);
            Assert.Contains("GPT-6 Sol", indicator.AccessibleDescription);
            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                RemainingPercent: 20,
                ResetsAt: now.AddHours(4.5),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));

            Assert.True(indicator.IsActive);
            Assert.Equal(UsagePace.High, indicator.Pace(now));
            Assert.Equal(ResetPhase.Normal, indicator.Reset(now));

            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                RemainingPercent: 20,
                ResetsAt: now.AddSeconds(30),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(ResetPhase.Imminent, indicator.Reset(now));

            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                RemainingPercent: 100,
                ResetsAt: now.AddHours(5),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: now));
            Assert.Equal(ResetPhase.Recent, indicator.Reset(now));

            using var dueIndicator = new UsageActivityIndicator();
            dueIndicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                RemainingPercent: 0,
                ResetsAt: now.AddMinutes(-2),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(ResetPhase.Normal, dueIndicator.Reset(now));

            using var restartedIndicator = new UsageActivityIndicator();
            restartedIndicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                RemainingPercent: 100,
                ResetsAt: now.AddHours(4).AddMinutes(58),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(ResetPhase.Recent, restartedIndicator.Reset(now));
        });

    [Fact]
    public Task RingRotationFollowsAllowanceUseEvenWhenIdle() =>
        StaTest.RunAsync(() =>
        {
            var now = DateTimeOffset.Now;
            using var indicator = new UsageActivityIndicator();
            var resetsAt = now.AddHours(2.5);
            void ShowRemaining(int remaining) => indicator.ShowAllowance(new(
                remaining, resetsAt, TimeSpan.FromHours(5), null));

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
            indicator.ShowAllowance(new(50, now.AddHours(2.5), TimeSpan.FromHours(5), null));
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
                RemainingPercent: 0,
                ResetsAt: DateTimeOffset.Now.AddHours(5),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));

            Assert.Equal(4, indicator.RingSweep);
        });
}
