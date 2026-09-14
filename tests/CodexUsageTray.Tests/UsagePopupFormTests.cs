using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class UsagePopupFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstShowKeepsTheConfiguredBottomPadding(bool compact)
    {
        RunInStaThread(() =>
        {
            using var popup = new UsagePopupForm { Opacity = 0 };
            popup.SetViewModeForScreenshot(compact);
            popup.CreateControl();

            popup.ShowNearTray();
            Application.DoEvents();

            var refreshButton = popup.Controls
                .Cast<Control>()
                .Single(control => control.AccessibleName == "Refresh usage");
            Assert.Equal(20, popup.ClientSize.Height - refreshButton.Bottom);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopeningKeepsThePopupAtItsInitialHeightAndVerticalPositionWhenFirstShown(bool compact)
    {
        const int SimulatedDpiHeightIncrease = 56;
        RunInStaThread(() =>
        {
            using var popup = new UsagePopupForm { Opacity = 0 };
            popup.SetViewModeForScreenshot(compact);
            popup.CreateControl();
            var firstShow = true;
            popup.VisibleChanged += (_, _) =>
            {
                if (popup.Visible && firstShow)
                {
                    firstShow = false;
                    popup.Height += SimulatedDpiHeightIncrease;
                }
            };

            popup.ShowNearTray();
            Application.DoEvents();
            var initialBounds = popup.Bounds;
            var workingArea = Screen.FromRectangle(initialBounds).WorkingArea;
            popup.Hide();
            Application.DoEvents();

            popup.ShowNearTray();
            Application.DoEvents();

            Assert.Equal(initialBounds.Height, popup.Height);
            Assert.Equal(initialBounds.Top, popup.Top);
            Assert.Equal(8, workingArea.Bottom - popup.Bottom);
        });
    }

    [Fact]
    public void WeeklyOnlyPresentationHidesFiveHourControlsAndRemovesTheirSpace()
    {
        RunInStaThread(() =>
        {
            var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
            var presentation = UsagePresentation.Create(
                new UsageSnapshot(
                    now,
                    now,
                    fiveHour: null,
                    new AllowanceWindow(40, TimeSpan.FromDays(7), now.AddDays(4)),
                    lifetimeTokens: null,
                    todayTokens: null,
                    plan: "pro",
                    limitName: "Codex"),
                now,
                CultureInfo.InvariantCulture);
            using var popup = new UsagePopupForm { Opacity = 0 };
            popup.ShowPresentation(presentation);
            popup.SetViewModeForScreenshot(compact: false);
            popup.Show();
            Application.DoEvents();

            var fiveHourTitle = popup.Controls.OfType<Label>()
                .Single(label => label.Text == "5-hour limit");
            var weeklyTitle = popup.Controls.OfType<Label>()
                .Single(label => label.Text == "Weekly limit");
            var updatedLabel = popup.Controls.OfType<Label>()
                .Single(label => label.Text.StartsWith("Updated ", StringComparison.Ordinal));
            var refreshButton = popup.Controls
                .Cast<Control>()
                .Single(control => control.AccessibleName == "Refresh usage");
            Assert.False(fiveHourTitle.Visible);
            Assert.Equal(86, weeklyTitle.Top);
            Assert.Single(popup.Controls.OfType<UsageProgressBar>(), bar => bar.Visible);
            Assert.True(popup.ClientSize.Height < 411);
            Assert.Equal(20, popup.ClientSize.Height - updatedLabel.Bottom);

            popup.SetViewModeForScreenshot(compact: true);

            Assert.Equal(64, weeklyTitle.Top);
            Assert.True(popup.ClientSize.Height < 141);
            Assert.Equal(20, popup.ClientSize.Height - refreshButton.Bottom);
        });
    }

    private static void RunInStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }
}
