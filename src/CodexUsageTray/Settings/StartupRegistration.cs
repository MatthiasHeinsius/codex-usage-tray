using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexUsageTray;

internal static class StartupRegistration
{
    private const string LegacyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "CodexUsageTray";
    private const string ShortcutFileName = "Codex Usage Tray.lnk";

    public static bool IsEnabled()
    {
        var executablePath = ExecutablePath();
        var shortcutPath = ShortcutPath();
        if (StartupShortcut.TargetsExecutable(shortcutPath, executablePath))
        {
            if (LegacyRegistrationExists())
            {
                try
                {
                    DeleteLegacyOrRollbackShortcut(DeleteLegacyRegistration, () => File.Delete(shortcutPath));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or COMException)
                {
                    return LegacyRegistrationTargets(executablePath);
                }
            }

            return true;
        }

        if (!LegacyRegistrationTargets(executablePath))
        {
            return false;
        }

        try
        {
            SetEnabled(enabled: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
        {
            // Keep a working legacy registration if shortcut migration is blocked.
        }

        return true;
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var hasLegacyRegistration = LegacyRegistrationExists();
            var shortcutPath = ShortcutPath();
            StartupShortcut.Create(shortcutPath, ExecutablePath());
            if (hasLegacyRegistration)
            {
                DeleteLegacyOrRollbackShortcut(DeleteLegacyRegistration, () => File.Delete(shortcutPath));
            }
        }
        else
        {
            File.Delete(ShortcutPath());
            DeleteLegacyRegistration();
        }
    }

    internal static void DeleteLegacyOrRollbackShortcut(Action deleteLegacy, Action rollbackShortcut)
    {
        try
        {
            deleteLegacy();
        }
        catch
        {
            rollbackShortcut();
            throw;
        }
    }

    private static bool LegacyRegistrationTargets(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, writable: false);
        return key?.GetValue(LegacyValueName) is string value
            && string.Equals(value, $"\"{executablePath}\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LegacyRegistrationExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, writable: false);
        return key?.GetValue(LegacyValueName) is not null;
    }

    private static void DeleteLegacyRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, writable: true);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static string ShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutFileName);

    private static string ExecutablePath() => Environment.ProcessPath
        ?? throw new InvalidOperationException("The application executable path is unavailable.");
}
