namespace CodexUsageTray.Tests;

public sealed class UsageActivityIndicatorTests
{
    [Fact]
    public Task CombinesRecentActivityWithAllowancePaceAndResetPhase() =>
        StaTest.RunAsync(() =>
        {
            var now = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            using var indicator = new UsageActivityIndicator();
            indicator.ShowSession(new CodexSessionActivity(DateTimeOffset.Now, CodexModel.Sol));
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
    public Task EasesRingMovementInAndOutAroundRecentActivity() =>
        StaTest.RunAsync(() =>
        {
            var now = DateTimeOffset.Now;
            using var indicator = new UsageActivityIndicator();
            indicator.ShowSession(new CodexSessionActivity(now, CodexModel.Terra));

            Assert.True(indicator.IsActive);
            indicator.Advance(now);
            Assert.Equal(0, indicator.ActivityMotion);

            indicator.Advance(now.AddMilliseconds(125));
            Assert.InRange(indicator.ActivityMotion, .49f, .51f);

            indicator.Advance(now.AddMilliseconds(250));
            Assert.Equal(1, indicator.ActivityMotion);

            indicator.Advance(now.AddSeconds(4));
            Assert.True(indicator.IsActive);

            indicator.Advance(now.AddSeconds(4).AddMilliseconds(1));
            Assert.False(indicator.IsActive);
            Assert.InRange(indicator.ActivityMotion, .99f, 1);

            indicator.Advance(now.AddSeconds(4).AddMilliseconds(251));
            Assert.InRange(indicator.ActivityMotion, .43f, .45f);

            indicator.Advance(now.AddSeconds(4).AddMilliseconds(501));
            Assert.False(indicator.IsActive);
            Assert.Equal(0, indicator.ActivityMotion);
        });

    [Fact]
    public Task KeepsAVisibleRedMarkerAtZeroAllowance() =>
        StaTest.RunAsync(() =>
        {
            using var indicator = new UsageActivityIndicator();
            indicator.ShowAllowance(new UsagePresentation.ActivityIndicatorPresentation(
                RemainingPercent: 0,
                ResetsAt: DateTimeOffset.Now.AddHours(5),
                WindowDuration: TimeSpan.FromHours(5),
                ResetObservedAt: null));
            using var bitmap = new Bitmap(indicator.Width, indicator.Height);

            indicator.DrawToBitmap(bitmap, indicator.ClientRectangle);

            Assert.Contains(
                Enumerable.Range(0, bitmap.Width)
                    .SelectMany(x => Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y))),
                color => color.R > 180 && color.G < 110 && color.B < 110);
        });
}
