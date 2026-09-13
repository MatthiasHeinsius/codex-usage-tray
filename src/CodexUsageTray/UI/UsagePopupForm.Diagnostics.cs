namespace CodexUsageTray;

internal sealed partial class UsagePopupForm
{
    internal static IReadOnlyList<string> RunLayoutDiagnostics()
    {
        var failures = new List<string>();
        using var popup = new UsagePopupForm();

        Check(
            popup.IsExtendedView == !PopupViewSettings.IsCompact(),
            "popup exposes its current view mode");
        Check(!popup.HeaderControlsOverlap(), "header controls do not overlap");
        Check(popup.InferenceDividerPaddingIsBalanced(), "inference divider padding is balanced");

        var screenBounds = new Rectangle(100, 50, 1_000, 750);
        var workingArea = new Rectangle(100, 50, 1_000, 700);
        var popupSize = new Size(440, 150);
        Check(
            SnapToScreen(new Point(109, 59), popupSize, screenBounds, workingArea)
                == new Point(108, 58),
            "popup snaps eight pixels inside its left and top borders");
        Check(
            SnapToScreen(new Point(101, 51), popupSize, screenBounds, workingArea)
                == new Point(100, 50),
            "popup also snaps flush with its left and top borders");
        Check(
            SnapToScreen(new Point(651, 591), popupSize, screenBounds, workingArea)
                == new Point(652, 592),
            "popup snaps eight pixels above the taskbar");
        Check(
            SnapToScreen(new Point(300, 599), popupSize, screenBounds, workingArea)
                == new Point(300, 600),
            "popup also snaps flush with the taskbar");
        Check(
            SnapToScreen(new Point(300, 620), popupSize, screenBounds, workingArea)
                == new Point(300, 620),
            "popup can move across the taskbar");
        Check(
            SnapToScreen(new Point(300, 641), popupSize, screenBounds, workingArea)
                == new Point(300, 642),
            "popup snaps eight pixels above the physical screen bottom");
        Check(
            SnapToScreen(new Point(300, 649), popupSize, screenBounds, workingArea)
                == new Point(300, 650),
            "popup also snaps flush with the physical screen bottom");
        Check(
            SnapToScreen(new Point(130, 90), popupSize, screenBounds, workingArea)
                == new Point(130, 90),
            "popup remains unsnapped away from screen borders");
        Check(
            GetLocationWhenShown(
                new Point(300, 620),
                pinned: true,
                new Point(900, 700),
                popupSize,
                workingArea)
                == new Point(300, 620),
            "pinned popup keeps its position when reopened");
        Check(
            GetLocationWhenShown(
                new Point(300, 620),
                pinned: false,
                new Point(900, 700),
                popupSize,
                workingArea)
                == new Point(480, 592),
            "unpinned popup returns to its tray position when reopened");

        popup.Location = new Point(-10_000, -10_000);
        popup.Show();
        try
        {
            popup.SetViewModeForScreenshot(compact: false);
            popup.ShowPresentation(UsagePresentation.CreateLoading(previous: null));
            Check(!popup.HeaderControlsOverlap(), "scaled header controls do not overlap");
            Check(popup.ContentPaddingIsUniform(), "popup content padding is uniform");
            Check(popup.InferenceDividerPaddingIsBalanced(), "scaled inference padding is balanced");

            var statusTextBottomClearance = popup.statusLabel.Height - popup.statusLabel.PreferredHeight;
            Check(
                statusTextBottomClearance >= 2,
                $"loading status keeps descender clearance ({statusTextBottomClearance}px)");
            var fixedLabelVerticalClearance = popup.Controls
                .OfType<Label>()
                .Where(label => !label.AutoSize)
                .Min(label => label.Height - TextRenderer.MeasureText(
                    label.Text,
                    label.Font,
                    Size.Empty,
                    TextFormatFlags.NoPadding).Height);
            Check(
                fixedLabelVerticalClearance >= 4,
                $"fixed-height labels keep vertical clearance ({fixedLabelVerticalClearance}px)");

            var extendedRefreshInset = popup.ClientSize.Height - popup.refreshButton.Bottom;
            popup.SetViewModeForScreenshot(compact: true);
            Check(popup.CompactRefreshLayoutIsCorrect(), "compact refresh button fits without extra height");
            Check(
                popup.ClientSize.Height - popup.refreshButton.Bottom == extendedRefreshInset,
                "refresh button stays fixed when changing view mode");

            var currentScreen = Screen.FromControl(popup);
            popup.Location = new Point(
                currentScreen.WorkingArea.Left + 100,
                currentScreen.WorkingArea.Top + 8);
            var snappedTop = popup.Top;
            popup.SetViewModeForScreenshot(compact: false, preserveBottom: true);
            Check(popup.Top == snappedTop, "view mode preserves an eight-pixel top inset");
        }
        finally
        {
            popup.Hide();
        }

        return failures;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }

    }

    private bool HeaderControlsOverlap() =>
            title.Left
            + TextRenderer.MeasureText(
                title.Text,
                title.Font,
                Size.Empty,
                TextFormatFlags.NoPadding).Width
            + 8
            > usagePageButton.Left
            || statusLabel.Bounds.IntersectsWith(usagePageButton.Bounds)
            || statusLabel.Bounds.IntersectsWith(viewModeButton.Bounds)
            || statusLabel.Bounds.IntersectsWith(pinButton.Bounds);

    private bool InferenceDividerPaddingIsBalanced() =>
        todayTitle.Top - limitsDivider.Bottom == inferenceDivider.Top - lifetimeTitle.Bottom;

    private bool CompactRefreshLayoutIsCorrect() =>
        compactView
        && refreshButton.Visible
        && !updatedLabel.Visible
        && ClientSize.Width - refreshButton.Right == ContentInset
        && ClientSize.Height - refreshButton.Bottom == ContentInset
        && weeklyReset.Right + 10 <= refreshButton.Left;

    private bool ContentPaddingIsUniform() =>
        fiveHourTitle.Left == Padding.Left
        && ClientSize.Width - fiveHourBar.Right == Padding.Right
        && ClientSize.Width - pinButton.Right == Padding.Right
        && pinButton.Top == Padding.Top
        && ClientSize.Height - refreshButton.Bottom == Padding.Bottom;
}
