using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class UsagePopupFormTests
{
    [Fact]
    public Task ExtendedLayoutKeepsControlsSeparatedAndLabelsReadable()
    {
        return StaTest.RunAsync(() =>
        {
            using var popup = ShowExtendedPopup();
            var title = ControlWithText<Label>(popup, "Codex usage");
            var status = ControlWithText<Label>(popup, "Reading your Codex account");
            var fiveHourTitle = ControlWithText<Label>(popup, "5-hour limit");
            var todayTitle = ControlWithText<Label>(popup, "Inference today");
            var lifetimeTitle = ControlWithText<Label>(popup, "Inference total");
            var usagePageButton = ControlWithAccessibleName<UsageLinkIconButton>(popup, "Open Codex usage page");
            var viewModeButton = popup.Controls.OfType<ViewModeIconButton>().Single();
            var pinButton = popup.Controls.OfType<PinIconButton>().Single();
            var refreshButton = ControlWithAccessibleName<RefreshIconButton>(popup, "Refresh usage");
            var fiveHourBar = popup.Controls.OfType<UsageProgressBar>().OrderBy(control => control.Top).First();
            var dividers = popup.Controls.OfType<Panel>().OrderBy(control => control.Top).ToArray();

            Assert.True(title.Right + 8 <= usagePageButton.Left);
            Assert.False(status.Bounds.IntersectsWith(usagePageButton.Bounds));
            Assert.False(status.Bounds.IntersectsWith(viewModeButton.Bounds));
            Assert.False(status.Bounds.IntersectsWith(pinButton.Bounds));
            Assert.Equal(todayTitle.Top - dividers[0].Bottom, dividers[1].Top - lifetimeTitle.Bottom);
            Assert.Equal(popup.Padding.Left, fiveHourTitle.Left);
            Assert.Equal(popup.Padding.Right, popup.ClientSize.Width - fiveHourBar.Right);
            Assert.Equal(popup.Padding.Right, popup.ClientSize.Width - pinButton.Right);
            Assert.Equal(popup.Padding.Top, pinButton.Top);
            Assert.Equal(popup.Padding.Bottom, popup.ClientSize.Height - refreshButton.Bottom);
            Assert.True(status.Height - status.PreferredHeight >= 2);
            Assert.All(
                popup.Controls.OfType<Label>().Where(label => !label.AutoSize),
                label => Assert.True(
                    label.Height - TextRenderer.MeasureText(
                        label.Text,
                        label.Font,
                        Size.Empty,
                        TextFormatFlags.NoPadding).Height >= 4,
                    $"{label.Text} does not have enough vertical clearance."));
        });
    }

    [Fact]
    public Task ChangingViewModeKeepsRefreshInsetsAndTopSnap()
    {
        return StaTest.RunAsync(() =>
        {
            using var popup = ShowExtendedPopup();
            var refreshButton = ControlWithAccessibleName<RefreshIconButton>(popup, "Refresh usage");
            var updatedLabel = ControlWithText<Label>(popup, "Not updated yet");
            var weeklyReset = popup.Controls
                .OfType<Label>()
                .Where(label => label.Text == "Reset time unavailable")
                .OrderBy(label => label.Top)
                .Last();
            var extendedRefreshInset = popup.ClientSize.Height - refreshButton.Bottom;

            popup.Controls.OfType<ViewModeIconButton>().Single().PerformClick();

            Assert.True(refreshButton.Visible);
            Assert.False(updatedLabel.Visible);
            Assert.Equal(popup.Padding.Right, popup.ClientSize.Width - refreshButton.Right);
            Assert.Equal(popup.Padding.Bottom, popup.ClientSize.Height - refreshButton.Bottom);
            Assert.Equal(extendedRefreshInset, popup.ClientSize.Height - refreshButton.Bottom);
            Assert.True(weeklyReset.Right + 10 <= refreshButton.Left);

            var screen = Screen.FromControl(popup);
            popup.Location = new Point(screen.WorkingArea.Left + 100, screen.WorkingArea.Top + 8);
            var snappedTop = popup.Top;

            popup.Controls.OfType<ViewModeIconButton>().Single().PerformClick();

            Assert.Equal(snappedTop, popup.Top);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FirstShowKeepsTheConfiguredBottomPadding(bool compact)
    {
        return StaTest.RunAsync(() =>
        {
            using var popup = new UsagePopupForm(initialCompactView: compact) { Opacity = 0 };
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
    public Task ReopeningKeepsThePopupAtItsInitialHeightAndVerticalPositionWhenFirstShown(bool compact)
    {
        const int SimulatedDpiHeightIncrease = 56;
        return StaTest.RunAsync(() =>
        {
            using var popup = new UsagePopupForm(initialCompactView: compact) { Opacity = 0 };
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
    public Task WeeklyOnlyPresentationHidesFiveHourControlsAndRemovesTheirSpace()
    {
        return StaTest.RunAsync(() =>
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
            using var popup = new UsagePopupForm(initialCompactView: false) { Opacity = 0 };
            popup.ShowPresentation(presentation);
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

            popup.Controls.OfType<ViewModeIconButton>().Single().PerformClick();

            Assert.Equal(64, weeklyTitle.Top);
            Assert.True(popup.ClientSize.Height < 141);
            Assert.Equal(20, popup.ClientSize.Height - refreshButton.Bottom);
        });
    }

    private static UsagePopupForm ShowExtendedPopup()
    {
        var popup = new UsagePopupForm(initialCompactView: false)
        {
            Location = new Point(-10_000, -10_000),
            Opacity = 0
        };
        popup.ShowPresentation(UsagePresentation.CreateLoading(previous: null));
        popup.Show();
        Application.DoEvents();
        return popup;
    }

    private static T ControlWithText<T>(UsagePopupForm popup, string text)
        where T : Control =>
        popup.Controls.OfType<T>().Single(control => control.Text == text);

    private static T ControlWithAccessibleName<T>(UsagePopupForm popup, string accessibleName)
        where T : Control =>
        popup.Controls.OfType<T>().Single(control => control.AccessibleName == accessibleName);
}
