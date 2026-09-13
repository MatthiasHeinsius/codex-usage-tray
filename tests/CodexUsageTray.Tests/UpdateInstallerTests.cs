using System.Diagnostics;
using System.Text;

namespace CodexUsageTray.Tests;

public sealed class UpdateInstallerTests
{
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

    [Fact]
    public void ElevatedApplyReplacesTheExecutableAndCleansTheStagedUpdate()
    {
        using var directory = new TemporaryDirectory("installer-apply");
        const string replacementText = "verified replacement executable";
        var (stagedPath, targetPath) = CreateInstallerFiles(
            directory,
            replacementText,
            "old executable");
        var interaction = new RecordingInstallerInteraction();

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--apply-update-elevated", "2147483647", stagedPath, targetPath],
            out var exitCode,
            interaction);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(replacementText), File.ReadAllBytes(targetPath));
        Assert.False(File.Exists(stagedPath));
        Assert.Null(interaction.ElevatedStartInfo);
        Assert.Equal(0, interaction.RestartAttempts);
    }

    [Fact]
    public void FailedRestartRestoresThePreviousExecutableAndReportsARecoverableFailure()
    {
        using var directory = new TemporaryDirectory("installer-rollback");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        var interaction = new RecordingInstallerInteraction(
            new InvalidOperationException("restart failed"));

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--apply-update", "2147483647", stagedPath, targetPath],
            out var exitCode,
            interaction);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal("previous executable", File.ReadAllText(targetPath));
        Assert.False(File.Exists(stagedPath));
        Assert.Equal(2, interaction.RestartAttempts);
        Assert.Null(interaction.ElevatedStartInfo);
        Assert.Contains("restart failed", interaction.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedApplyReportsAReadOnlyTargetAndDiscardsTheStagedUpdate()
    {
        using var directory = new TemporaryDirectory("installer-read-only-apply");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        File.SetAttributes(targetPath, FileAttributes.ReadOnly);
        var interaction = new RecordingInstallerInteraction();

        try
        {
            var handled = UpdateInstaller.TryHandleCommandLine(
                ["--apply-update-elevated", "2147483647", stagedPath, targetPath],
                out var exitCode,
                interaction);

            Assert.True(handled);
            Assert.Equal(1, exitCode);
            Assert.Equal("previous executable", File.ReadAllText(targetPath));
            Assert.False(File.Exists(stagedPath));
            Assert.Equal(0, interaction.RestartAttempts);
            Assert.Contains("still read-only", interaction.FailureMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void LaunchRemovesTheCopiedHelperWhenTheInstallerCannotStart()
    {
        using var directory = new TemporaryDirectory("installer-launch");
        var stagedPath = directory.FilePath("download.tmp");
        var targetPath = directory.FilePath("CodexUsageTray.exe");
        var update = new StagedApplicationUpdate(new Version(1, 4, 0), stagedPath);
        File.WriteAllText(stagedPath, "replacement executable");
        var interaction = new RecordingInstallerInteraction(
            installerFailure: new InvalidOperationException("installer start failed"));

        var failure = Assert.Throws<InvalidOperationException>(
            () => UpdateInstaller.Launch(update, 42, targetPath, interaction));

        Assert.Equal("installer start failed", failure.Message);
        Assert.NotNull(interaction.InstallerStartInfo);
        Assert.False(File.Exists(interaction.InstallerStartInfo.FileName));
        Assert.False(interaction.InstallerStartInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, interaction.InstallerStartInfo.WindowStyle);
        Assert.Equal(
            ["--apply-update", "42", stagedPath, targetPath],
            interaction.InstallerStartInfo.ArgumentList);
    }

    [Fact]
    public void FailedRollbackPreservesTheBackupAndReportsAnUnrecoverableFailure()
    {
        using var directory = new TemporaryDirectory("installer-failed-rollback");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        var interaction = new RecordingInstallerInteraction(
            restartFailure: new InvalidOperationException("restart failed"),
            beforeRestart: path =>
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
            });

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--apply-update", "2147483647", stagedPath, targetPath],
            out var exitCode,
            interaction);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(stagedPath));
        Assert.Equal(1, interaction.RestartAttempts);
        Assert.Contains("The previous executable could not be restored", interaction.FailureMessage, StringComparison.Ordinal);
        var backupPath = BackupPathFrom(interaction.FailureMessage!);
        try
        {
            Assert.Equal("previous executable", File.ReadAllText(backupPath));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Theory]
    [InlineData((int)UpdateInstallerResult.Succeeded, 1)]
    [InlineData((int)UpdateInstallerResult.RecoverableFailure, 1)]
    [InlineData((int)UpdateInstallerResult.UnrecoverableFailure, 0)]
    public void ReadOnlyApplyUsesElevationAndRestartsOnlyAfterARecoverableResult(
        int elevatedExitCode,
        int expectedRestartAttempts)
    {
        using var directory = new TemporaryDirectory("installer-elevation");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        File.SetAttributes(targetPath, FileAttributes.ReadOnly);
        var interaction = new RecordingInstallerInteraction(
            elevatedResult: (UpdateInstallerResult)elevatedExitCode);

        try
        {
            var handled = UpdateInstaller.TryHandleCommandLine(
                ["--apply-update", "2147483647", stagedPath, targetPath],
                out var exitCode,
                interaction);

            Assert.True(handled);
            Assert.Equal(elevatedExitCode, exitCode);
            Assert.Equal(expectedRestartAttempts, interaction.RestartAttempts);
            var startInfo = Assert.IsType<ProcessStartInfo>(interaction.ElevatedStartInfo);
            Assert.True(startInfo.UseShellExecute);
            Assert.Equal("runas", startInfo.Verb);
            Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), startInfo.FileName);
            Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath), startInfo.WorkingDirectory);
            Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
            Assert.Equal(
                ["--apply-update-elevated", "2147483647", stagedPath, targetPath],
                startInfo.ArgumentList);
        }
        finally
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
    }

    private sealed class RecordingInstallerInteraction(
        Exception? restartFailure = null,
        Exception? installerFailure = null,
        Action<string>? beforeRestart = null,
        UpdateInstallerResult elevatedResult = UpdateInstallerResult.Succeeded)
        : IUpdateInstallerInteraction
    {
        public int RestartAttempts { get; private set; }
        public string? FailureMessage { get; private set; }
        public ProcessStartInfo? InstallerStartInfo { get; private set; }
        public ProcessStartInfo? ElevatedStartInfo { get; private set; }

        public void StartInstaller(ProcessStartInfo startInfo)
        {
            InstallerStartInfo = startInfo;
            if (installerFailure is not null)
            {
                throw installerFailure;
            }
        }

        public UpdateInstallerResult RunElevatedInstaller(ProcessStartInfo startInfo)
        {
            ElevatedStartInfo = startInfo;
            return elevatedResult;
        }

        public void StartApplication(string targetPath)
        {
            RestartAttempts++;
            beforeRestart?.Invoke(targetPath);
            if (restartFailure is not null)
            {
                throw restartFailure;
            }
        }

        public void ShowFailure(string message) => FailureMessage = message;
    }

    private static (string StagedPath, string TargetPath) CreateInstallerFiles(
        TemporaryDirectory directory,
        string stagedContents = "replacement executable",
        string targetContents = "previous executable")
    {
        var stagedPath = directory.FilePath("download.tmp");
        var targetPath = directory.FilePath("CodexUsageTray.exe");
        File.WriteAllText(stagedPath, stagedContents);
        File.WriteAllText(targetPath, targetContents);
        return (stagedPath, targetPath);
    }

    private static string BackupPathFrom(string failureMessage)
    {
        const string marker = "Its backup remains at ";
        var start = failureMessage.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return failureMessage[(start + marker.Length)..].TrimEnd('.');
    }
}
