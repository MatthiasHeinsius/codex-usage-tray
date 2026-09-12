using Microsoft.Win32;

namespace CodexUsageTray;

internal static class ApplicationUpdateSettings
{
    private const string RegistryPath = @"Software\CodexUsageTray";
    private const string AutomaticCheckEnabledName = "AutomaticUpdateChecks";

    internal static bool IsAutomaticCheckEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return IsAutomaticCheckEnabled(key?.GetValue(AutomaticCheckEnabledName));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void SetAutomaticCheckEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(AutomaticCheckEnabledName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    internal static bool IsAutomaticCheckEnabled(object? storedValue) =>
        storedValue is int value && value != 0;
}
