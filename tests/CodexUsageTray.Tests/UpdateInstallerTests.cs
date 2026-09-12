using System.Text;

namespace CodexUsageTray.Tests;

public sealed class UpdateInstallerTests
{
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
        var directory = Path.Combine(Path.GetTempPath(), $"CodexUsageTray-installer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.tmp");
        var targetPath = Path.Combine(directory, "CodexUsageTray.exe");
        var replacement = Encoding.UTF8.GetBytes("verified replacement executable");
        File.WriteAllBytes(sourcePath, replacement);
        File.WriteAllText(targetPath, "old executable");

        try
        {
            UpdateInstaller.ReplaceFileContentsWhenAvailable(sourcePath, targetPath);

            Assert.Equal(replacement, File.ReadAllBytes(targetPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
