namespace CodexUsageTray.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void EnableCreatesAReadableShortcutForTheRequestedExecutable()
    {
        using var directory = new TemporaryDirectory("startup-registration");
        var executablePath = directory.FilePath("CodexUsageTray.exe");
        var shortcutPath = directory.FilePath("Codex Usage Tray.lnk");
        File.WriteAllText(executablePath, string.Empty);

        StartupRegistration.SetEnabled(enabled: true, shortcutPath, executablePath);

        var details = StartupRegistration.ReadShortcut(shortcutPath);
        Assert.NotNull(details);
        Assert.Equal(executablePath, details.Value.TargetPath, ignoreCase: true);
        Assert.Equal(executablePath, details.Value.IconPath, ignoreCase: true);
        Assert.Equal(0, details.Value.IconIndex);
        Assert.True(StartupRegistration.IsEnabled(shortcutPath, executablePath));
        Assert.False(StartupRegistration.IsEnabled(shortcutPath, directory.FilePath("other.exe")));
    }

    [Fact]
    public void DisableDeletesTheShortcut()
    {
        using var directory = new TemporaryDirectory("disabled-startup-registration");
        var executablePath = directory.FilePath("CodexUsageTray.exe");
        var shortcutPath = directory.FilePath("Codex Usage Tray.lnk");
        File.WriteAllText(executablePath, string.Empty);
        StartupRegistration.SetEnabled(enabled: true, shortcutPath, executablePath);

        StartupRegistration.SetEnabled(enabled: false, shortcutPath, executablePath);

        Assert.False(File.Exists(shortcutPath));
        Assert.False(StartupRegistration.IsEnabled(shortcutPath, executablePath));
    }

    [Fact]
    public void MissingShortcutIsDisabled()
    {
        using var directory = new TemporaryDirectory("missing-startup-registration");

        Assert.False(StartupRegistration.IsEnabled(
            directory.FilePath("missing.lnk"),
            directory.FilePath("CodexUsageTray.exe")));
    }
}
