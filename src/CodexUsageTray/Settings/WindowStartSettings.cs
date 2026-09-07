using Microsoft.Win32;

namespace CodexUsageTray;

internal static class WindowStartSettings
{
    private const string RegistryPath = @"Software\CodexUsageTray";
    private const string EnabledName = "AutoStartUsageWindows";
    private const string FiveHourResetName = "LastStartedFiveHourReset";
    private const string WeeklyResetName = "LastStartedWeeklyReset";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        return key?.GetValue(EnabledName) is not int value || value != 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(EnabledName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public static bool ShouldStartFiveHour(UsageWindow? window, DateTimeOffset now) =>
        ShouldStart(window, now, FiveHourResetName);

    public static bool ShouldStartWeekly(UsageWindow? window, DateTimeOffset now) =>
        ShouldStart(window, now, WeeklyResetName);

    public static void MarkStarted(bool fiveHour, bool weekly, UsageSnapshot snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (fiveHour && snapshot.FiveHour?.ResetsAt is { } fiveHourReset)
        {
            key.SetValue(FiveHourResetName, fiveHourReset.ToUnixTimeSeconds(), RegistryValueKind.QWord);
        }

        if (weekly && snapshot.Weekly?.ResetsAt is { } weeklyReset)
        {
            key.SetValue(WeeklyResetName, weeklyReset.ToUnixTimeSeconds(), RegistryValueKind.QWord);
        }
    }

    internal static bool IsExpiredAndUnstarted(UsageWindow? window, DateTimeOffset now, long? lastStartedReset)
    {
        if (window?.ResetsAt is not { } reset || reset > now)
        {
            return false;
        }

        return lastStartedReset != reset.ToUnixTimeSeconds();
    }

    private static bool ShouldStart(UsageWindow? window, DateTimeOffset now, string registryName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        var stored = key?.GetValue(registryName);
        long? lastStarted = stored switch
        {
            long value => value,
            int value => value,
            _ => null
        };
        return IsExpiredAndUnstarted(window, now, lastStarted);
    }
}
