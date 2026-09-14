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

        var popupLayoutFailures = UsagePopupForm.RunLayoutDiagnostics();
        if (popupLayoutFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "Self-test failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, popupLayoutFailures.Select(failure => $"- {failure}")));
        }

        var startupTestDirectory = Path.Combine(Path.GetTempPath(), $"CodexUsageTray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(startupTestDirectory);
        try
        {
            var shortcutPath = Path.Combine(startupTestDirectory, "Codex Usage Tray.lnk");
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Self-test process path is unavailable.");
            Assert(!StartupRegistration.IsEnabled(shortcutPath, executablePath),
                "missing startup shortcut is disabled");
            StartupRegistration.SetEnabled(enabled: true, shortcutPath, executablePath);
            Assert(File.Exists(shortcutPath), "startup shortcut is created");
            Assert(StartupRegistration.IsEnabled(shortcutPath, executablePath),
                "startup shortcut targets this executable");
            var shortcut = StartupRegistration.ReadShortcut(shortcutPath);
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
