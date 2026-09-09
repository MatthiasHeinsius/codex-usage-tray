namespace CodexUsageTray;

internal static class SelfTest
{
    public static int Run()
    {
        using var lengthAhead = new LengthAheadStream(length: 10, position: 3);
        using var copiedBytes = new MemoryStream();
        Assert(LocalTokenUsageReader.CopyUnreadBytes(lengthAhead, copiedBytes) == 3,
            "local token reader advances by consumed bytes");

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
        Assert(
            !TrayApplicationContext.ShouldShowAfterTrayClick(visibleWhenMousePressed: true),
            "tray click closes a popup that deactivated during the click");
        Assert(
            !TrayApplicationContext.ShouldHandleTrayClick(
                currentTimestamp: 1_100,
                previousTimestamp: 1_000,
                doubleClickTime: 500),
            "second click of a tray double-click does not toggle again");
        Assert(
            TrayApplicationContext.ShouldHandleTrayClick(
                currentTimestamp: 1_501,
                previousTimestamp: 1_000,
                doubleClickTime: 500),
            "later tray click toggles normally");
        popup.Location = new Point(-10_000, -10_000);
        popup.Show();
        try
        {
            popup.SetViewModeForScreenshot(compact: false);
            popup.SetLoading(loading: true);
            Assert(
                popup.StatusTextBottomClearance >= 2,
                $"loading status keeps descender clearance ({popup.StatusTextBottomClearance}px)");
            var extendedRefreshInset = popup.RefreshButtonBottomInset;
            popup.SetViewModeForScreenshot(compact: true);
            Assert(popup.CompactRefreshLayoutIsCorrect, "compact refresh button fits without extra height");
            Assert(popup.RefreshButtonBottomInset == extendedRefreshInset,
                "refresh button stays fixed when changing view mode");
        }
        finally
        {
            popup.Hide();
        }

        var expiredWindow = new AllowanceWindow(100, TimeSpan.FromHours(5), DateTimeOffset.FromUnixTimeSeconds(100));
        Assert(WindowStartSettings.IsExpiredAndUnstarted(expiredWindow, DateTimeOffset.FromUnixTimeSeconds(101), null),
            "expired window needs start");
        Assert(!WindowStartSettings.IsExpiredAndUnstarted(expiredWindow, DateTimeOffset.FromUnixTimeSeconds(101), 100),
            "completed window start is not repeated");
        Assert(!WindowStartSettings.IsExpiredAndUnstarted(expiredWindow, DateTimeOffset.FromUnixTimeSeconds(99), null),
            "future window is not started");
        var freshUnusedWindow = new AllowanceWindow(0, TimeSpan.FromHours(5), DateTimeOffset.FromUnixTimeSeconds(500));
        Assert(
            WindowStartSettings.ShouldStartAfterRefresh(
                expiredWindow,
                freshUnusedWindow,
                DateTimeOffset.FromUnixTimeSeconds(101),
                lastStartedReset: null),
            "expired window remains pending when refresh rolls to an unused window");
        var freshUsedWindow = freshUnusedWindow with { UsedPercent = 1 };
        Assert(
            !WindowStartSettings.ShouldStartAfterRefresh(
                expiredWindow,
                freshUsedWindow,
                DateTimeOffset.FromUnixTimeSeconds(101),
                lastStartedReset: null),
            "expired window is complete when refreshed window has usage");
        Assert(
            !WindowStartSettings.ShouldStartAfterRefresh(
                expiredWindow,
                freshUnusedWindow,
                DateTimeOffset.FromUnixTimeSeconds(101),
                lastStartedReset: 100),
            "recorded expired window is not started twice after refresh");
        Assert(!WindowStartSettings.IsEnabledValue(null), "window auto-start defaults to off");
        Assert(WindowStartSettings.IsEnabledValue(1), "window auto-start accepts enabled value");
        Assert(!WindowStartSettings.IsEnabledValue(0), "window auto-start accepts disabled value");

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

        var shortcutRolledBack = false;
        try
        {
            StartupRegistration.DeleteLegacyOrRollbackShortcut(
                () => throw new UnauthorizedAccessException("Synthetic registry failure."),
                () => shortcutRolledBack = true);
        }
        catch (UnauthorizedAccessException)
        {
            // Expected test failure path.
        }

        Assert(shortcutRolledBack, "failed legacy cleanup rolls back the startup shortcut");

        var localUsageLines = new[]
        {
            "{\"timestamp\":\"2026-09-07T08:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":1200}}}",
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":800}}}",
            "{\"timestamp\":\"2026-09-06T09:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":9999}}}",
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{}}"
        };
        var localUsage = LocalTokenUsageReader.SumLinesForDate(localUsageLines, new DateOnly(2026, 9, 7));
        Assert(localUsage.Found && localUsage.Tokens == 2000, "local daily inference fallback");

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

    private sealed class LengthAheadStream(long length, long position) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; } = position;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
