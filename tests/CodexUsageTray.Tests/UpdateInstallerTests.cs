using System.Text;

namespace CodexUsageTray.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public void WritableTargetDoesNotNeedElevation()
    {
        using var directory = new TemporaryDirectory("writable-installer-target");
        var path = directory.FilePath("CodexUsageTray.exe");
        File.WriteAllText(path, "installed executable");

        Assert.True(UpdateInstaller.HasWriteAccess(path));
    }

    [Fact]
    public void ReadOnlyTargetNeedsElevation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CodexUsageTray-read-only-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "installed executable");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Assert.False(UpdateInstaller.HasWriteAccess(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void ElevatedInstallerRequestsAdministratorAccess()
    {
        var startInfo = UpdateInstaller.CreateElevatedInstallerStartInfo(
            @"C:\Temp\update helper.exe",
            42,
            @"C:\Temp\download.tmp",
            @"C:\Program Files\CodexUsageTray\CodexUsageTray.exe");

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(Path.GetFullPath(@"C:\Temp\update helper.exe"), startInfo.FileName);
        Assert.Equal(@"C:\Temp", startInfo.WorkingDirectory);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(
            [
                "--apply-update-elevated",
                "42",
                @"C:\Temp\download.tmp",
                @"C:\Program Files\CodexUsageTray\CodexUsageTray.exe"
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    public void ReplacementOverwritesExistingFileContents()
    {
        using var directory = new TemporaryDirectory("installer-replacement");
        var sourcePath = directory.FilePath("source.tmp");
        var targetPath = directory.FilePath("CodexUsageTray.exe");
        var replacement = Encoding.UTF8.GetBytes("verified replacement executable");
        File.WriteAllBytes(sourcePath, replacement);
        File.WriteAllText(targetPath, "old executable");

        UpdateInstaller.ReplaceFileContentsWhenAvailable(sourcePath, targetPath);

        Assert.Equal(replacement, File.ReadAllBytes(targetPath));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public void CleanupArgumentDeletesTheHelperAndContinuesApplicationStartup()
    {
        using var directory = new TemporaryDirectory("installer-cleanup");
        var helperPath = directory.FilePath("update-helper.exe");
        File.WriteAllText(helperPath, "helper");

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--cleanup-update", helperPath],
            out var exitCode);

        Assert.False(handled);
        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(helperPath));
    }
}
