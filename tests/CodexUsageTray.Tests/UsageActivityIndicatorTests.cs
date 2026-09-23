using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class UsageActivityIndicatorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SwitchingAllowanceDoesNotInferAReset(bool startsWithFiveHour) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(PresentAllowance(startsWithFiveHour, 40, now.AddHours(2), now).Popup.ActivityIndicator);
            indicator.ShowAllowance(PresentAllowance(!startsWithFiveHour, 0, now.AddHours(3), now).Popup.ActivityIndicator);

            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
            indicator.Advance(now);
            Assert.DoesNotContain("reset", indicator.AccessibleDescription);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SwitchingAllowanceDiscardsWaitingHistoryEvenWhenResetTimesMatch(bool startsWithFiveHour) =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var reset = now.AddHours(3);
            using var indicator = new UsageActivityIndicator();
            var original = PresentAllowance(startsWithFiveHour, 0, reset, now, resetObserved: true);
            indicator.ShowAllowance(original.Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));

            indicator.ShowAllowance(PresentAllowance(!startsWithFiveHour, 0, reset, now).Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));

            indicator.ShowAllowance(PresentAllowance(startsWithFiveHour, 0, reset, now).Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
        });

    [Fact]
    public Task MissingAllowanceDiscardsWaitingHistory() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var reset = now.AddHours(3);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(PresentAllowance(true, 0, reset, now, resetObserved: true).Popup.ActivityIndicator);
            indicator.ShowAllowance(UsagePresentation.ActivityIndicatorPresentation.Unavailable);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));

            indicator.ShowAllowance(PresentAllowance(true, 0, reset, now).Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
        });

    [Fact]
    public Task CurrentAllowanceKeepsItsOwnResetAndActivationAcrossRepeatedPresentations() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
            var reset = now.AddDays(5);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(PresentAllowance(true, 40, now.AddHours(3), now).Popup.ActivityIndicator);
            var weeklyReset = PresentAllowance(false, 0, reset, now, resetObserved: true);
            foreach (var presentation in new UsagePresentation[]
                {
                    weeklyReset,
                    weeklyReset.WithoutNotices(),
                    UsagePresentation.CreateLoading(weeklyReset),
                    UsagePresentation.CreateFailed(weeklyReset, "Offline"),
                    PresentAllowance(false, 0, reset, now)
                })
            {
                indicator.ShowAllowance(presentation.Popup.ActivityIndicator);
                Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
            }

            indicator.Advance(now);
            Assert.Contains("ready to activate", indicator.AccessibleDescription);
            var activated = PresentAllowance(false, 0, reset, now, activatedResetAt: reset);
            indicator.ShowAllowance(activated.Popup.ActivityIndicator);
            indicator.ShowAllowance(activated.Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
            using var restarted = new UsageActivityIndicator();
            restarted.ShowAllowance(activated.Popup.ActivityIndicator);
            Assert.Equal(AllowanceFlashPhase.Normal, restarted.Flash(now));
        });

    private static UsagePresentation.Ready PresentAllowance(
        bool fiveHour,
        int usedPercent,
        DateTimeOffset reset,
        DateTimeOffset now,
        bool resetObserved = false,
        DateTimeOffset? activatedResetAt = null)
    {
        var window = new AllowanceWindow(usedPercent, fiveHour ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7), reset);
        var snapshot = new UsageSnapshot(now, null, fiveHour ? window : null, fiveHour ? null : window,
            null, null, "plus", null);
        var events = new AllowanceWindowActivationResult(
            resetObserved ? fiveHour ? AllowanceWindows.FiveHour : AllowanceWindows.Weekly : AllowanceWindows.None,
            AllowanceWindows.None);
        return UsagePresentation.Create(snapshot, events, false, now, CultureInfo.InvariantCulture, activatedResetAt);
    }

    [Fact]
    public Task CombinesRecentActivityWithAllowancePaceAndFlashPhase() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowSession(new CodexSessionActivity(DateTimeOffset.Now, CodexModel.Sol, "6"));
            indicator.Advance(DateTimeOffset.Now);
            Assert.Contains("GPT-6 Sol", indicator.AccessibleDescription);
            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 20,
                ResetsAt: now.AddHours(4.5),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));

            Assert.True(indicator.IsActive);
            Assert.Equal(UsagePace.High, indicator.Pace(now));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));

            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 20,
                ResetsAt: now.AddMinutes(15).AddSeconds(1),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));

            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 20,
                ResetsAt: now.AddMinutes(15),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(AllowanceFlashPhase.BeforeReset, indicator.Flash(now));

            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 20,
                ResetsAt: now.AddSeconds(30),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(AllowanceFlashPhase.BeforeReset, indicator.Flash(now));

            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 100,
                ResetsAt: now.AddHours(5),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: now));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));

            using var dueIndicator = new UsageActivityIndicator();
            dueIndicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 0,
                ResetsAt: now.AddMinutes(-2),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(AllowanceFlashPhase.UsedUp, dueIndicator.Flash(now));

            using var restartedIndicator = new UsageActivityIndicator();
            restartedIndicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                WindowKind: AllowanceWindowKind.FiveHour,
                RemainingPercent: 100,
                ResetsAt: now.AddHours(4).AddMinutes(58),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            Assert.Equal(AllowanceFlashPhase.AfterReset, restartedIndicator.Flash(now));
        });

    [Fact]
    public Task ResetFlashLastsUntilFiveMinutesAfterActivation() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, now.AddMinutes(-10), TimeSpan.FromHours(5), now.AddMinutes(-15)));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));

            var activatedAt = now.AddMinutes(10);
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, activatedAt.AddHours(5), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(activatedAt.AddMinutes(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));

            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 99, activatedAt.AddHours(5), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));

            using var restarted = new UsageActivityIndicator();
            restarted.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 100, activatedAt.AddHours(5), TimeSpan.FromHours(5), null,
                ActivatedResetAt: activatedAt.AddHours(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, restarted.Flash(activatedAt.AddMinutes(10)));
        });

    [Fact]
    public Task FullAllowanceInAnOlderWindowDoesNotWaitForActivation() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now.AddHours(3), TimeSpan.FromHours(5), null));

            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(now));
        });

    [Fact]
    public Task ResetWithAllowanceRemainingFlashesUntilActivation() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 60, now, TimeSpan.FromHours(5), null));
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now.AddHours(5), TimeSpan.FromHours(5), null));

            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now.AddMinutes(20)));

            var activatedAt = now.AddMinutes(20);
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, activatedAt.AddHours(5), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(activatedAt.AddMinutes(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));
        });

    [Fact]
    public Task UnusedWindowResetWaitsForActivation() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now, TimeSpan.FromHours(5), null));
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now.AddHours(5), TimeSpan.FromHours(5), null));

            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now.AddMinutes(20)));

            var activatedAt = now.AddMinutes(20);
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, activatedAt.AddHours(5), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));
        });

    [Fact]
    public Task ResetFlashWaitsForObservedActivationEvenWhenUsageChanges() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var reset = now.AddHours(5);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, reset, TimeSpan.FromHours(5), now));
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 99, reset, TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now.AddMinutes(20)));

            var activatedAt = now.AddMinutes(20);
            indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, 99, activatedAt.AddHours(5), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(activatedAt.AddMinutes(5)));
            Assert.Equal(AllowanceFlashPhase.Normal, indicator.Flash(activatedAt.AddMinutes(5).AddSeconds(1)));
        });

    [Fact]
    public Task UsedUpFlashYieldsToTheFinalFifteenMinuteResetFlash() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 0, now.AddHours(2), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.UsedUp, indicator.Flash(now));

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 0, null, TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.UsedUp, indicator.Flash(now));

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 0, now.AddMinutes(15), TimeSpan.FromHours(5), null));
            Assert.Equal(AllowanceFlashPhase.BeforeReset, indicator.Flash(now));

            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 100, now.AddHours(5), TimeSpan.FromHours(5), now));
            Assert.Equal(AllowanceFlashPhase.AfterReset, indicator.Flash(now));
        });

    [Fact]
    public Task DemoShowsFastAndSlowBurnsInOneMinute() =>
        StaTest.RunAsync(() =>
        {
            var start = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            var stages = new (int Run, int Second, int Remaining, AllowanceFlashPhase Phase)[]
            {
                (0, 0, 100, AllowanceFlashPhase.Normal),
                (0, 8, 0, AllowanceFlashPhase.UsedUp),
                (0, 14, 0, AllowanceFlashPhase.BeforeReset),
                (0, 19, 100, AllowanceFlashPhase.AfterReset),
                (0, 23, 100, AllowanceFlashPhase.AfterReset),
                (0, 27, 99, AllowanceFlashPhase.Normal),
                (1, 0, 100, AllowanceFlashPhase.Normal),
                (1, 8, 60, AllowanceFlashPhase.Normal),
                (1, 14, 60, AllowanceFlashPhase.BeforeReset),
                (1, 19, 100, AllowanceFlashPhase.AfterReset),
                (1, 23, 100, AllowanceFlashPhase.AfterReset),
                (1, 27, 99, AllowanceFlashPhase.Normal)
            };

            foreach (var (run, second, expectedRemaining, expectedPhase) in stages)
            {
                var runStart = start.AddSeconds(run * 30);
                var frame = AllowanceDemo.FrameAt(runStart, TimeSpan.FromSeconds(second), run);
                indicator.ShowAllowance(frame.Allowance);
                Assert.Equal(expectedRemaining, frame.Allowance.RemainingPercent);
                Assert.Equal(expectedPhase, indicator.Flash(runStart.AddSeconds(second)));
            }
        });

    [Fact]
    public Task RingRotationFollowsAllowanceUseEvenWhenIdle() =>
        StaTest.RunAsync(() =>
        {
            var now = DateTimeOffset.Now;
            using var indicator = new UsageActivityIndicator();
            var resetsAt = now.AddHours(2.5);
            void ShowRemaining(int remaining) => indicator.ShowAllowance(new(
                AllowanceWindowKind.FiveHour, remaining, resetsAt, TimeSpan.FromHours(5), null));

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
            indicator.ShowAllowance(new(AllowanceWindowKind.FiveHour, 50, now.AddHours(2.5), TimeSpan.FromHours(5), null));
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
                ResetObservedAt: null));

            Assert.Equal(4, indicator.RingSweep);
        });
}
