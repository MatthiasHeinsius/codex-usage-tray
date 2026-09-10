using Microsoft.Win32;

namespace CodexUsageTray;

internal sealed class RegistryAllowanceWindowActivationSettings : IAllowanceWindowActivationSettings
{
    private const string RegistryPath = @"Software\CodexUsageTray";
    private const string ActivationEnabledName = "AutoStartUsageWindows";
    private const string NotificationsEnabledName = "AllowanceNotifications";
    private const string FiveHourResetName = "LastStartedFiveHourReset";
    private const string WeeklyResetName = "LastStartedWeeklyReset";

    public bool ActivationEnabled
    {
        get => ReadBoolean(ActivationEnabledName);
        set => WriteBoolean(ActivationEnabledName, value);
    }

    public bool NotificationsEnabled
    {
        get => ReadBoolean(NotificationsEnabledName);
        set => WriteBoolean(NotificationsEnabledName, value);
    }

    public DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        var stored = key?.GetValue(ResetName(window));
        var seconds = stored switch
        {
            long value => value,
            int value => value,
            _ => (long?)null
        };
        if (seconds is not { } unixSeconds)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(ResetName(window), reset.ToUnixTimeSeconds(), RegistryValueKind.QWord);
    }

    private static bool ReadBoolean(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        return key?.GetValue(name) is int value && value != 0;
    }

    private static void WriteBoolean(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }

    private static string ResetName(AllowanceWindowKind window) =>
        window == AllowanceWindowKind.FiveHour ? FiveHourResetName : WeeklyResetName;
}
