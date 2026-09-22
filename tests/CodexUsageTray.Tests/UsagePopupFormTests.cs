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
            var title = ControlWithText<Label>(popup, "Codex Usage");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AccountStatusShowsTheActiveModelOrIdleState(bool compact)
    {
        return StaTest.RunAsync(() =>
        {
            var now = DateTimeOffset.Now;
            var presentation = UsagePresentation.Create(
                new UsageSnapshot(
                    now,
                    now,
                    fiveHour: null,
                    weekly: null,
                    lifetimeTokens: null,
                    todayTokens: null,
                    plan: "plus",
                    limitName: "Codex"),
                now,
                CultureInfo.InvariantCulture);
            using var popup = new UsagePopupForm(initialCompactView: compact)
            {
                Location = new Point(-10_000, -10_000),
                Opacity = 0
            };
            popup.ShowPresentation(presentation);
            popup.Show();
            Application.DoEvents();

            popup.SetActivityForScreenshot(new CodexSessionActivity(now, CodexModel.Sol, "6"));
            var activeStatus = ControlWithText<Label>(popup, "Plus · using GPT-6 Sol");
            Assert.True(activeStatus.Visible);
            Assert.True(activeStatus.PreferredWidth <= activeStatus.Width);

            popup.SetActivityForScreenshot(new CodexSessionActivity(now, CodexModel.Unknown));
            Assert.NotNull(ControlWithText<Label>(popup, "Plus · using unknown model"));

            popup.SetActivityForScreenshot(new CodexSessionActivity(now.AddMinutes(-1), CodexModel.Sol));
            Assert.NotNull(ControlWithText<Label>(popup, "Plus · idle"));
        });
    }

    [Fact]
    public Task ActivityIndicatorUsesATransparentOverlayOutsideThePopup()
    {
        return StaTest.RunAsync(() =>
        {
            using var popup = ShowExtendedPopup();
            var title = ControlWithText<Label>(popup, "Codex Usage");
            var overlay = Assert.Single(popup.OwnedForms);
            var indicator = Assert.Single(overlay.Controls.OfType<UsageActivityIndicator>());
            const int ringInset = 13;
            var leftOverhang = popup.Left - (overlay.Left + ringInset);
            var topOverhang = popup.Top - (overlay.Top + ringInset);

            Assert.InRange(Math.Abs(leftOverhang - topOverhang), 0, 1);
            Assert.True(leftOverhang > 0);
            Assert.Equal(
                popup.Top + title.Top + (title.Height / 2),
                overlay.Top + indicator.Top + (indicator.Height / 2));
            Assert.Equal(overlay.BackColor, overlay.TransparencyKey);
            Assert.NotEqual(popup.BackColor, overlay.TransparencyKey);
            Assert.Equal(indicator.Size + new Size(1, 1), overlay.ClientSize);
            Assert.Equal(FormBorderStyle.None, overlay.FormBorderStyle);
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

            var fiveHourTitle = ControlWithText<Label>(popup, "5-hour limit");
            var weeklyTitle = ControlWithText<Label>(popup, "Weekly limit");
            var fiveHourValue = popup.Controls.OfType<Label>()
                .Where(label => label.Text == "Unavailable" && label.TextAlign == ContentAlignment.MiddleRight)
                .OrderBy(label => label.Top)
                .First();
            Assert.True(refreshButton.Visible);
            Assert.False(updatedLabel.Visible);
            Assert.True(
                fiveHourTitle.Right <= fiveHourValue.Left,
                $"Title {fiveHourTitle.Bounds} overlaps value {fiveHourValue.Bounds}.");
            Assert.Equal(popup.Padding.Left, fiveHourTitle.Left);
            Assert.Equal(86, fiveHourTitle.Top);
            Assert.Equal(fiveHourTitle.Left, weeklyTitle.Left);
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
            var extendedHeight = popup.ClientSize.Height;

            popup.Controls.OfType<ViewModeIconButton>().Single().PerformClick();

            Assert.Equal(86, weeklyTitle.Top);
            Assert.True(popup.ClientSize.Height < extendedHeight);
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
