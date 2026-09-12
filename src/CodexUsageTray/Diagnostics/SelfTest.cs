namespace CodexUsageTray;

internal static class SelfTest
{
    public static int Run()
    {
        Assert(
            TrayIconRenderer.StaticFiveHourRemaining == 66
                && TrayIconRenderer.StaticWeeklyRemaining == 75,
            "static icon uses the requested ring positions");
        using var icon = TrayIconRenderer.Create(65, 7);
        var expectedIconSize = Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height);
        Assert(icon.Width == expectedIconSize && icon.Height == expectedIconSize,
            "dual-ring tray icon matches the system size");
        var iconData = TrayIconRenderer.CreateIcoData(65, 7);
        Assert(BitConverter.ToUInt16(iconData, 4) == 9, "tray icon has nine resolution variants");
        Assert(iconData[6 + (8 * 16)] == 0, "tray icon includes a 256-pixel variant");
        for (var index = 0; index < 9; index++)
        {
            Assert(BitConverter.ToUInt16(iconData, 6 + (index * 16) + 6) == 32,
                $"tray icon variant {index + 1} uses 32-bit color");
        }

        using var popup = new UsagePopupForm();
        Assert(popup.IsExtendedView == !PopupViewSettings.IsCompact(), "popup exposes its current view mode");
        Assert(!popup.HeaderControlsOverlap, "header controls do not overlap");
        Assert(popup.InferenceDividerPaddingIsBalanced, "inference divider padding is balanced");
        var screenBounds = new Rectangle(100, 50, 1_000, 750);
        var workingArea = new Rectangle(100, 50, 1_000, 700);
        var popupSize = new Size(440, 150);
        Assert(
            UsagePopupForm.SnapToScreen(new Point(109, 59), popupSize, screenBounds, workingArea)
                == new Point(108, 58),
            "popup snaps eight pixels inside its left and top borders");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(101, 51), popupSize, screenBounds, workingArea)
                == new Point(100, 50),
            "popup also snaps flush with its left and top borders");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(651, 591), popupSize, screenBounds, workingArea)
                == new Point(652, 592),
            "popup snaps eight pixels above the taskbar");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(300, 599), popupSize, screenBounds, workingArea)
                == new Point(300, 600),
            "popup also snaps flush with the taskbar");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(300, 620), popupSize, screenBounds, workingArea)
                == new Point(300, 620),
            "popup can move across the taskbar");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(300, 641), popupSize, screenBounds, workingArea)
                == new Point(300, 642),
            "popup snaps eight pixels above the physical screen bottom");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(300, 649), popupSize, screenBounds, workingArea)
                == new Point(300, 650),
            "popup also snaps flush with the physical screen bottom");
        Assert(
            UsagePopupForm.SnapToScreen(new Point(130, 90), popupSize, screenBounds, workingArea)
                == new Point(130, 90),
            "popup remains unsnapped away from screen borders");
        Assert(
            UsagePopupForm.GetLocationWhenShown(
                new Point(300, 620),
                pinned: true,
                new Point(900, 700),
                popupSize,
                workingArea)
                == new Point(300, 620),
            "pinned popup keeps its position when reopened");
        Assert(
            UsagePopupForm.GetLocationWhenShown(
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
            Assert(!popup.HeaderControlsOverlap, "scaled header controls do not overlap");
            Assert(popup.ContentPaddingIsUniform, "popup content padding is uniform");
            Assert(popup.InferenceDividerPaddingIsBalanced, "scaled inference padding is balanced");
            Assert(
                popup.StatusTextBottomClearance >= 2,
                $"loading status keeps descender clearance ({popup.StatusTextBottomClearance}px)");
            Assert(
                popup.FixedLabelVerticalClearance >= 4,
                $"fixed-height labels keep vertical clearance ({popup.FixedLabelVerticalClearance}px)");
            var extendedRefreshInset = popup.RefreshButtonBottomInset;
            popup.SetViewModeForScreenshot(compact: true);
            Assert(popup.CompactRefreshLayoutIsCorrect, "compact refresh button fits without extra height");
            Assert(popup.RefreshButtonBottomInset == extendedRefreshInset,
                "refresh button stays fixed when changing view mode");
            var currentScreen = Screen.FromControl(popup);
            popup.Location = new Point(
                currentScreen.WorkingArea.Left + 100,
                currentScreen.WorkingArea.Top + 8);
            var snappedTop = popup.Top;
            popup.SetViewModeForScreenshot(compact: false, preserveBottom: true);
            Assert(popup.Top == snappedTop, "view mode preserves an eight-pixel top inset");
        }
        finally
        {
            popup.Hide();
        }

        var startupTestDirectory = Path.Combine(Path.GetTempPath(), $"CodexUsageTray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(startupTestDirectory);
        try
        {
            var shortcutPath = Path.Combine(startupTestDirectory, "Codex Usage Tray.lnk");
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Self-test process path is unavailable.");
            Assert(!StartupShortcut.TargetsExecutable(shortcutPath, executablePath),
                "missing startup shortcut is disabled");
            StartupShortcut.Create(shortcutPath, executablePath);
            Assert(File.Exists(shortcutPath), "startup shortcut is created");
            Assert(StartupShortcut.TargetsExecutable(shortcutPath, executablePath),
                "startup shortcut targets this executable");
            var shortcut = StartupShortcut.Read(shortcutPath);
            Assert(shortcut is { IconIndex: 0 }
                && string.Equals(shortcut.Value.IconPath, executablePath, StringComparison.OrdinalIgnoreCase),
                "startup shortcut uses the executable icon");
        }
        finally
        {
            Directory.Delete(startupTestDirectory, recursive: true);
        }

        Console.WriteLine("All self-tests passed.");
        return 0;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {name}");
        }
    }

}
