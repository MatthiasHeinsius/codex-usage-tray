using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexUsageTray.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public void LaunchRejectsAnUpdateChangedAfterDownloadVerification()
    {
        using var directory = new TemporaryDirectory("installer-changed-download");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        using var update = new StagedApplicationUpdate(new Version(1, 4, 0), stagedPath, HashFile(stagedPath));
        File.WriteAllText(stagedPath, "changed after verification");
        var interaction = new RecordingInstallerInteraction();

        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Launch(update, 42, targetPath, interaction));

        Assert.Null(interaction.InstallerStartInfo);
        Assert.Equal("previous executable", File.ReadAllText(targetPath));
    }

    [Fact]
    public void VerifiedHelperCanStartWhileItsFilesAreProtectedFromChanges()
    {
        using var directory = new TemporaryDirectory("installer-verified-launch");
        var stagedPath = directory.FilePath("download.tmp");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), stagedPath);
        using var update = new StagedApplicationUpdate(new Version(1, 4, 0), stagedPath, HashFile(stagedPath));
        var interaction = new RecordingInstallerInteraction(beforeInstaller: startInfo =>
        {
            foreach (var path in new[] { stagedPath, startInfo.FileName })
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "tampered"));
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Equal(update.ExpectedHash, HashFile(path));
            }

            Assert.Equal(update.ExpectedHash, startInfo.ArgumentList[4]);
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.CreateNoWindow = true;
            using var process = Process.Start(startInfo)!;
            try
            {
                Assert.True(process.WaitForExit(5_000));
                Assert.Equal(0, process.ExitCode);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
        });

        try
        {
            UpdateInstaller.Launch(update, 42, directory.FilePath("app.exe"), interaction);
        }
        finally
        {
            if (interaction.InstallerStartInfo is { } startInfo)
            {
                File.Delete(startInfo.FileName);
            }
        }
    }

    [Theory]
    [InlineData("--apply-update", 1)]
    [InlineData("--apply-update-elevated", 0)]
    public void ApplyRejectsAnUpdateChangedAfterHelperHandoff(string command, int restarts)
    {
        using var directory = new TemporaryDirectory("installer-changed-handoff");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        var expectedHash = HashFile(stagedPath);
        File.WriteAllText(stagedPath, "changed after handoff");
        var interaction = new RecordingInstallerInteraction();

        Assert.True(UpdateInstaller.TryHandleCommandLine(
            [command, "2147483647", stagedPath, targetPath, expectedHash], out var exitCode, interaction));

        Assert.Equal(1, exitCode);
        Assert.Equal("previous executable", File.ReadAllText(targetPath));
        Assert.False(File.Exists(stagedPath));
        Assert.Equal(restarts, interaction.RestartAttempts);
        Assert.Contains("SHA-256", interaction.FailureMessage, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(directory.RootPath));
    }

    [Fact]
    public void ElevationRequiresTheRunningHelperToMatchTheReleaseChecksum()
    {
        using var directory = new TemporaryDirectory("installer-helper-checksum");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        File.SetAttributes(targetPath, FileAttributes.ReadOnly);
        var interaction = new RecordingInstallerInteraction();

        try
        {
            Assert.True(UpdateInstaller.TryHandleCommandLine(
                ["--apply-update", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
                out var exitCode, interaction));

            Assert.Equal(1, exitCode);
            Assert.Null(interaction.ElevatedStartInfo);
            Assert.Equal("previous executable", File.ReadAllText(targetPath));
            Assert.False(File.Exists(stagedPath));
            Assert.Contains("SHA-256", interaction.FailureMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ElevatedSuccessWithAnUnexpectedExecutableDoesNotRestartIt()
    {
        using var directory = new TemporaryDirectory("installer-changed-elevated-target");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        File.Copy(Environment.ProcessPath!, stagedPath, overwrite: true);
        File.SetAttributes(targetPath, FileAttributes.ReadOnly);
        var interaction = new RecordingInstallerInteraction(beforeElevation: _ =>
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
            File.WriteAllText(targetPath, "changed after elevated install");
        });

        try
        {
            Assert.True(UpdateInstaller.TryHandleCommandLine(
                ["--apply-update", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
                out var exitCode, interaction));

            Assert.Equal(2, exitCode);
            Assert.Equal(0, interaction.RestartAttempts);
            Assert.Contains("SHA-256", interaction.FailureMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
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

    [Fact]
    public void CleanupArgumentWaitsForARunningHelperToExitBeforeDeletingIt()
    {
        using var directory = new TemporaryDirectory("installer-running-cleanup");
        var helperPath = directory.FilePath("update-helper.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), helperPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("127.0.0.1");
        using var helper = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The cleanup test helper did not start.");
        try
        {
            Assert.False(helper.WaitForExit(milliseconds: 100));

            var handled = UpdateInstaller.TryHandleCommandLine(
                ["--cleanup-update", helperPath],
                out var exitCode);

            Assert.False(handled);
            Assert.Equal(0, exitCode);
            Assert.True(helper.HasExited);
            Assert.False(File.Exists(helperPath));
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill();
                helper.WaitForExit();
            }

            if (File.Exists(helperPath))
            {
                File.Delete(helperPath);
            }
        }
    }

    [Fact]
    public async Task ApplyWaitsForTheRunningApplicationBeforeReplacingItsExecutable()
    {
        using var directory = new TemporaryDirectory("installer-running-application");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "CodexUsageTray.ProcessFixture.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("hold");
        startInfo.ArgumentList.Add(directory.FilePath("application.pid"));
        using var application = Process.Start(startInfo)!;
        Task<(bool Handled, int ExitCode)>? installation = null;
        var enteredInstaller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitedBeforeReplacement = false;
        var restartedContents = new List<string>();
        var interaction = new RecordingInstallerInteraction(
            beforeRestart: path => restartedContents.Add(File.ReadAllText(path)));
        try
        {
            Assert.Equal("ready", await application.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var expectedHash = HashFile(stagedPath);
            installation = Task.Run(() =>
            {
                enteredInstaller.SetResult();
                var handled = UpdateInstaller.TryHandleCommandLine(
                    ["--apply-update", application.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        stagedPath, targetPath, expectedHash],
                    out var exitCode,
                    interaction,
                    (prepared, target, backup) =>
                    {
                        exitedBeforeReplacement = application.HasExited;
                        File.Replace(prepared, target, backup);
                    });
                return (handled, exitCode);
            }, TestContext.Current.CancellationToken);
            await enteredInstaller.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var stillRunning = Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            Assert.Same(stillRunning, await Task.WhenAny(installation, stillRunning));
            Assert.False(application.HasExited);
            Assert.Equal("previous executable", File.ReadAllText(targetPath));
            Assert.True(File.Exists(stagedPath));
            Assert.Empty(Directory.GetFiles(directory.RootPath, ".CodexUsageTray-*"));
            Assert.Empty(restartedContents);

            application.Kill(entireProcessTree: true);
            await application.WaitForExitAsync(TestContext.Current.CancellationToken);
            var result = await installation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(result.Handled);
            Assert.Equal((int)UpdateInstallerResult.Succeeded, result.ExitCode);
            Assert.True(exitedBeforeReplacement);
            Assert.Equal("replacement executable", File.ReadAllText(targetPath));
            Assert.Equal(["replacement executable"], restartedContents);
            Assert.False(File.Exists(stagedPath));
            Assert.Empty(Directory.GetFiles(directory.RootPath, ".CodexUsageTray-*"));
            Assert.Null(interaction.FailureMessage);
            Assert.Null(interaction.ElevatedStartInfo);
        }
        finally
        {
            if (!application.HasExited)
            {
                application.Kill(entireProcessTree: true);
                await application.WaitForExitAsync(CancellationToken.None);
            }
            if (installation is not null)
            {
                await installation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
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
            ["--apply-update-elevated", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
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
    public void ApplyReplacesAnExecutableThatIsOpenForReadingButAllowsRename()
    {
        using var directory = new TemporaryDirectory("installer-open-target");
        const string replacementText = "verified replacement executable";
        var (stagedPath, targetPath) = CreateInstallerFiles(
            directory,
            replacementText,
            "old executable");
        var interaction = new RecordingInstallerInteraction();
        using var targetLock = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--apply-update-elevated", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
            out var exitCode,
            interaction);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(replacementText), File.ReadAllBytes(targetPath));
        Assert.False(File.Exists(stagedPath));
        Assert.Null(interaction.FailureMessage);
    }

    [Fact]
    public async Task ApplyWaitsForAnExecutableLockThatTemporarilyDeniesRename()
    {
        using var directory = new TemporaryDirectory("installer-temporary-lock");
        const string replacementText = "verified replacement executable";
        var (stagedPath, targetPath) = CreateInstallerFiles(
            directory,
            replacementText,
            "old executable");
        var interaction = new RecordingInstallerInteraction();
        var targetLock = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var releaseLock = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
                targetLock.Dispose();
            },
            TestContext.Current.CancellationToken);

        try
        {
            var handled = UpdateInstaller.TryHandleCommandLine(
                ["--apply-update-elevated", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
                out var exitCode,
                interaction);

            Assert.True(handled);
            Assert.Equal(0, exitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(replacementText), File.ReadAllBytes(targetPath));
            Assert.False(File.Exists(stagedPath));
            Assert.Null(interaction.FailureMessage);
        }
        finally
        {
            targetLock.Dispose();
            await releaseLock;
        }
    }

    [Fact]
    public void InstalledExecutableCannotBeChangedBeforeRestartAndFailedRestartRollsBack()
    {
        using var directory = new TemporaryDirectory("installer-protected-target-rollback");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        var replacementBlocked = false;
        var interaction = new RecordingInstallerInteraction(
            restartFailure: new InvalidOperationException("restart failed"),
            beforeRestart: path =>
            {
                if (!replacementBlocked)
                {
                    Assert.Throws<IOException>(() => File.Delete(path));
                    Assert.Throws<IOException>(() => File.WriteAllText(path, "tampered"));
                    replacementBlocked = true;
                }
            });

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--apply-update", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
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
                ["--apply-update-elevated", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
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
        File.WriteAllText(stagedPath, "replacement executable");
        var update = new StagedApplicationUpdate(new Version(1, 4, 0), stagedPath, HashFile(stagedPath));
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
            ["--apply-update", "42", stagedPath, targetPath, HashFile(stagedPath)],
            interaction.InstallerStartInfo.ArgumentList);
    }

    [Fact]
    public void FailedRollbackPreservesTheBackupAndReportsAnUnrecoverableFailure()
    {
        using var directory = new TemporaryDirectory("installer-failed-rollback");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        FileStream? targetLock = null;
        var interaction = new RecordingInstallerInteraction(
            restartFailure: new InvalidOperationException("restart failed"),
            beforeRestart: path => targetLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));

        bool handled;
        int exitCode;
        try
        {
            handled = UpdateInstaller.TryHandleCommandLine(
                ["--apply-update", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
                out exitCode,
                interaction);
        }
        finally
        {
            targetLock?.Dispose();
        }

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
        File.Copy(Environment.ProcessPath!, stagedPath, overwrite: true);
        File.SetAttributes(targetPath, FileAttributes.ReadOnly);
        string? helperHash = null;
        Exception? helperLockFailure = null;
        var interaction = new RecordingInstallerInteraction(
            elevatedResult: (UpdateInstallerResult)elevatedExitCode,
            beforeElevation: startInfo =>
            {
                helperHash = HashFile(startInfo.FileName);
                helperLockFailure = Record.Exception(() =>
                {
                    using var exclusive = File.Open(startInfo.FileName, FileMode.Open, FileAccess.Read, FileShare.None);
                });
                if (elevatedExitCode == 0)
                {
                    File.SetAttributes(targetPath, FileAttributes.Normal);
                    File.Copy(stagedPath, targetPath, overwrite: true);
                }
            },
            beforeRestart: path =>
            {
                if (elevatedExitCode == 0)
                {
                    Assert.Throws<IOException>(() => File.Delete(path));
                    Assert.Throws<IOException>(() => File.WriteAllText(path, "tampered"));
                }
            });

        try
        {
            var handled = UpdateInstaller.TryHandleCommandLine(
                ["--apply-update", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
                out var exitCode,
                interaction);

            Assert.True(handled);
            Assert.Equal(elevatedExitCode, exitCode);
            Assert.Equal(expectedRestartAttempts, interaction.RestartAttempts);
            Assert.Equal(HashFile(stagedPath), helperHash);
            Assert.IsType<IOException>(helperLockFailure);
            var startInfo = Assert.IsType<ProcessStartInfo>(interaction.ElevatedStartInfo);
            Assert.True(startInfo.UseShellExecute);
            Assert.Equal("runas", startInfo.Verb);
            Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), startInfo.FileName);
            Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath), startInfo.WorkingDirectory);
            Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
            Assert.Equal(
                ["--apply-update-elevated", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
                startInfo.ArgumentList);
        }
        finally
        {
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PartialReplacementFailureRestoresTheBackupWithoutElevation(bool targetMissing, bool accessDenied)
    {
        using var directory = new TemporaryDirectory("installer-partial-replacement");
        var (stagedPath, targetPath) = CreateInstallerFiles(directory);
        var restartedContents = new List<string>();
        var interaction = new RecordingInstallerInteraction(
            beforeRestart: path => restartedContents.Add(File.ReadAllText(path)));
        var replacementAttempts = 0;

        var handled = UpdateInstaller.TryHandleCommandLine(
            ["--apply-update", "2147483647", stagedPath, targetPath, HashFile(stagedPath)],
            out var exitCode,
            interaction,
            (prepared, target, backup) =>
            {
                replacementAttempts++;
                // Model ReplaceFileW failing after it has moved the old executable to its backup.
                File.Move(target, backup!);
                if (!targetMissing)
                {
                    File.Move(prepared, target);
                }

                if (accessDenied)
                {
                    throw new UnauthorizedAccessException("Partial replacement failed.");
                }
                throw new IOException("Partial replacement failed.");
            });

        Assert.True(handled);
        Assert.Equal((int)UpdateInstallerResult.RecoverableFailure, exitCode);
        Assert.Equal(1, replacementAttempts);
        Assert.Equal("previous executable", File.ReadAllText(targetPath));
        Assert.Equal(["previous executable"], restartedContents);
        Assert.Null(interaction.ElevatedStartInfo);
        Assert.Contains("Partial replacement failed.", interaction.FailureMessage, StringComparison.Ordinal);
        Assert.Equal([targetPath], Directory.GetFiles(directory.RootPath));
    }

    private sealed class RecordingInstallerInteraction(
        Exception? restartFailure = null,
        Exception? installerFailure = null,
        Action<string>? beforeRestart = null,
        UpdateInstallerResult elevatedResult = UpdateInstallerResult.Succeeded,
        Action<ProcessStartInfo>? beforeInstaller = null,
        Action<ProcessStartInfo>? beforeElevation = null)
        : IUpdateInstallerInteraction
    {
        public int RestartAttempts { get; private set; }
        public string? FailureMessage { get; private set; }
        public ProcessStartInfo? InstallerStartInfo { get; private set; }
        public ProcessStartInfo? ElevatedStartInfo { get; private set; }

        public void StartInstaller(ProcessStartInfo startInfo)
        {
            InstallerStartInfo = startInfo;
            beforeInstaller?.Invoke(startInfo);
            if (installerFailure is not null)
            {
                throw installerFailure;
            }
        }

        public UpdateInstallerResult RunElevatedInstaller(ProcessStartInfo startInfo)
        {
            ElevatedStartInfo = startInfo;
            beforeElevation?.Invoke(startInfo);
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

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string BackupPathFrom(string failureMessage)
    {
        const string marker = "Its backup remains at ";
        var start = failureMessage.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return failureMessage[(start + marker.Length)..].TrimEnd('.');
    }
}
