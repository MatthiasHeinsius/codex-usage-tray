using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace CodexUsageTray;

internal static class StartupShortcut
{
    public static void Create(string shortcutPath, string executablePath)
    {
        var directory = Path.GetDirectoryName(shortcutPath)
            ?? throw new InvalidOperationException("The startup shortcut directory is unavailable.");
        Directory.CreateDirectory(directory);

        var comObject = (IShellLinkW)(object)new ShellLink();
        try
        {
            comObject.SetPath(executablePath);
            comObject.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? string.Empty);
            comObject.SetDescription("Codex Usage Tray");
            comObject.SetIconLocation(executablePath, 0);
            ((IPersistFile)comObject).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    public static bool TargetsExecutable(string shortcutPath, string executablePath)
    {
        var details = Read(shortcutPath);
        return details is not null
            && PathsEqual(details.Value.TargetPath, executablePath);
    }

    internal static StartupShortcutDetails? Read(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
        {
            return null;
        }

        var comObject = (IShellLinkW)(object)new ShellLink();
        try
        {
            ((IPersistFile)comObject).Load(shortcutPath, 0);
            var target = new StringBuilder(32_768);
            comObject.GetPath(target, target.Capacity, IntPtr.Zero, flags: 0);
            var iconPath = new StringBuilder(32_768);
            comObject.GetIconLocation(iconPath, iconPath.Capacity, out var iconIndex);
            return new StartupShortcutDetails(target.ToString(), iconPath.ToString(), iconIndex);
        }
        catch (Exception exception) when (
            exception is COMException or IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first),
        Path.GetFullPath(second),
        StringComparison.OrdinalIgnoreCase);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumCharacters,
            IntPtr findData,
            uint flags);

        void GetIdList(out IntPtr itemIdList);
        void SetIdList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumCharacters);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumCharacters);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumCharacters);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int showCommand);
        void SetShowCommand(int showCommand);
        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maximumCharacters,
            out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

internal readonly record struct StartupShortcutDetails(string TargetPath, string IconPath, int IconIndex);
