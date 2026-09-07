using Microsoft.Win32;

namespace CodexUsageTray;

internal static class PopupViewSettings
{
    private const string RegistryPath = @"Software\CodexUsageTray";
    private const string CompactName = "CompactPopup";

    public static bool IsCompact()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(CompactName) is int value && value != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void SetCompact(bool compact)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            key.SetValue(CompactName, compact ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException)
        {
            // The view still changes for the current process if Windows blocks persistence.
        }
    }
}
