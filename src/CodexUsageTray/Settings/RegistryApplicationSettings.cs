using System.Security;
using Microsoft.Win32;

namespace CodexUsageTray;

internal sealed class RegistryApplicationSettings : IAllowanceWindowActivationSettings
{
    private const string CompactName = "CompactPopup";
    private const string AutomaticCheckEnabledName = "AutomaticUpdateChecks";
    private const string ActivationEnabledName = "AutoStartUsageWindows";
    private const string NotificationsEnabledName = "AllowanceNotifications";
    private const string FiveHourResetName = "LastStartedFiveHourReset";
    private const string WeeklyResetName = "LastStartedWeeklyReset";
    private readonly string registryPath;

    internal static RegistryApplicationSettings Current { get; } = new(@"Software\CodexUsageTray");

    internal RegistryApplicationSettings(string registryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        this.registryPath = registryPath;
    }

    internal bool CompactPopup
    {
        get => ReadBoolean(CompactName);
        set
        {
            try
            {
                WriteBoolean(CompactName, value);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
            {
                // The view still changes for this process when Windows blocks persistence.
            }
        }
    }

    internal bool AutomaticUpdateChecksEnabled
    {
        get => ReadBoolean(AutomaticCheckEnabledName);
        set => WriteBoolean(AutomaticCheckEnabledName, value);
    }

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
        var stored = ReadValue(ResetName(window));
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
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
        key.SetValue(ResetName(window), reset.ToUnixTimeSeconds(), RegistryValueKind.QWord);
    }

    private object? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false);
        return key?.GetValue(name);
    }

    private bool ReadBoolean(string name)
    {
        try
        {
            return ReadValue(name) is int value && value != 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private void WriteBoolean(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }

    private static string ResetName(AllowanceWindowKind window) =>
        window == AllowanceWindowKind.FiveHour ? FiveHourResetName : WeeklyResetName;
}
