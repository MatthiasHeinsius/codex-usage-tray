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
        return IsEnabledValue(key?.GetValue(EnabledName));
    }

    internal static bool IsEnabledValue(object? stored) => stored is int value && value != 0;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(EnabledName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public static bool ShouldStartFiveHour(UsageWindow? window, DateTimeOffset now) =>
        ShouldStart(window, now, FiveHourResetName);

    public static bool ShouldStartWeekly(UsageWindow? window, DateTimeOffset now) =>
        ShouldStart(window, now, WeeklyResetName);

    public static bool ShouldStartFiveHourAfterRefresh(
        UsageWindow? observedWindow,
        UsageWindow? refreshedWindow,
        DateTimeOffset now) =>
        ShouldStartAfterRefresh(observedWindow, refreshedWindow, now, ReadLastStartedReset(FiveHourResetName));

    public static bool ShouldStartWeeklyAfterRefresh(
        UsageWindow? observedWindow,
        UsageWindow? refreshedWindow,
        DateTimeOffset now) =>
        ShouldStartAfterRefresh(observedWindow, refreshedWindow, now, ReadLastStartedReset(WeeklyResetName));

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

    internal static bool ShouldStartAfterRefresh(
        UsageWindow? observedWindow,
        UsageWindow? refreshedWindow,
        DateTimeOffset now,
        long? lastStartedReset)
    {
        if (!IsExpiredAndUnstarted(observedWindow, now, lastStartedReset))
        {
            return false;
        }

        return refreshedWindow?.ResetsAt is not { } refreshedReset
            || refreshedReset <= now
            || refreshedWindow.UsedPercent <= 0;
    }

    private static bool ShouldStart(UsageWindow? window, DateTimeOffset now, string registryName)
        => IsExpiredAndUnstarted(window, now, ReadLastStartedReset(registryName));

    private static long? ReadLastStartedReset(string registryName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        var stored = key?.GetValue(registryName);
        return stored switch
        {
            long value => value,
            int value => value,
            _ => null
        };
    }
}
