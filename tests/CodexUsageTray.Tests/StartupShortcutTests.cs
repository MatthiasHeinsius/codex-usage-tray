namespace CodexUsageTray.Tests;

public sealed class StartupShortcutTests
{
    [Fact]
    public void CreateWritesAReadableShortcutForTheRequestedExecutable()
    {
        using var directory = new TemporaryDirectory("startup-shortcut");
        var executablePath = directory.FilePath("CodexUsageTray.exe");
        var shortcutPath = directory.FilePath("Codex Usage Tray.lnk");
        File.WriteAllText(executablePath, string.Empty);

        StartupShortcut.Create(shortcutPath, executablePath);

        var details = StartupShortcut.Read(shortcutPath);
        Assert.NotNull(details);
        Assert.Equal(executablePath, details.Value.TargetPath, ignoreCase: true);
        Assert.Equal(executablePath, details.Value.IconPath, ignoreCase: true);
        Assert.Equal(0, details.Value.IconIndex);
        Assert.True(StartupShortcut.TargetsExecutable(shortcutPath, executablePath));
        Assert.False(StartupShortcut.TargetsExecutable(shortcutPath, directory.FilePath("other.exe")));
    }

    [Fact]
    public void ReadReturnsNullForAMissingShortcut()
    {
        using var directory = new TemporaryDirectory("missing-shortcut");

        Assert.Null(StartupShortcut.Read(directory.FilePath("missing.lnk")));
    }
}
