using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace CodexUsageTray;

internal static class UpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string ApplyElevatedArgument = "--apply-update-elevated";
    private const string CleanupArgument = "--cleanup-update";
    private const int FileOperationAttempts = 50;
    private static readonly TimeSpan FileOperationRetryDelay = TimeSpan.FromMilliseconds(100);

    internal static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        if (args is [ApplyArgument or ApplyElevatedArgument, var processIdText, var stagedPath, var targetPath]
            && int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            var elevated = string.Equals(args[0], ApplyElevatedArgument, StringComparison.Ordinal);
            exitCode = Apply(
                processId,
                stagedPath,
                targetPath,
                allowElevation: !elevated,
                restartApplication: !elevated);
            return true;
        }

        if (args is [CleanupArgument, var helperPath])
        {
            DeleteHelperWhenAvailable(helperPath);
        }

        exitCode = 0;
        return false;
    }

    internal static void Launch(ApplicationUpdate update, int processId, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var helperPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexUsageTray-update-helper-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(update.StagedPath, helperPath, overwrite: false);
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(ApplyArgument);
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(Path.GetFullPath(update.StagedPath));
            startInfo.ArgumentList.Add(Path.GetFullPath(targetPath));
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The update installer did not start.");
        }
        catch
        {
            ApplicationUpdater.TryDelete(helperPath);
            throw;
        }
    }

    private static int Apply(
        int processId,
        string stagedPath,
        string targetPath,
        bool allowElevation,
        bool restartApplication)
    {
        var backupPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexUsageTray-update-backup-{Guid.NewGuid():N}.exe");
        var applicationExited = false;
        var backupReady = false;
        try
        {
            WaitForProcessToExit(processId);
            applicationExited = true;
            if (!HasWriteAccess(targetPath))
            {
                if (allowElevation)
                {
                    return ApplyWithElevation(processId, stagedPath, targetPath);
                }

                throw new UnauthorizedAccessException(
                    "Administrator access was granted, but the installed executable is still read-only.");
            }

            File.Copy(targetPath, backupPath, overwrite: false);
            backupReady = true;
            ReplaceFileContentsWhenAvailable(stagedPath, targetPath);
            if (!FilesMatch(stagedPath, targetPath))
            {
                throw new InvalidDataException("The installed executable does not match the downloaded update.");
            }

            if (restartApplication)
            {
                StartApplication(targetPath);
            }

            ApplicationUpdater.TryDelete(stagedPath);
            ApplicationUpdater.TryDelete(backupPath);
            return 0;
        }
        catch (Exception exception)
        {
            var failure = exception;
            var canRestart = applicationExited;
            if (backupReady)
            {
                try
                {
                    ReplaceFileContentsWhenAvailable(backupPath, targetPath);
                }
                catch (Exception restoreException)
                {
                    canRestart = TryFilesMatch(backupPath, targetPath);
                    if (!canRestart)
                    {
                        failure = new InvalidOperationException(
                            $"{exception.Message}\n\nThe previous executable could not be restored: "
                                + $"{restoreException.Message}\n\nIts backup remains at {backupPath}.",
                            exception);
                    }
                }
            }

            ApplicationUpdater.TryDelete(stagedPath);
            if (canRestart)
            {
                ApplicationUpdater.TryDelete(backupPath);
            }

            MessageBox.Show(
                $"The update could not be installed.\n\n{failure.Message}",
                "Codex usage update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            if (restartApplication && canRestart)
            {
                TryRestartExistingApplication(targetPath);
            }

            return canRestart ? 1 : 2;
        }
    }

    internal static bool HasWriteAccess(string targetPath)
    {
        try
        {
            using var stream = new FileStream(
                targetPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ApplyWithElevation(int processId, string stagedPath, string targetPath)
    {
        var helperPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The update helper path is unavailable.");
        var startInfo = CreateElevatedInstallerStartInfo(
            helperPath,
            processId,
            stagedPath,
            targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The elevated update installer did not start.");
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            StartApplication(targetPath);
        }
        else if (process.ExitCode == 1)
        {
            TryRestartExistingApplication(targetPath);
        }

        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateElevatedInstallerStartInfo(
        string helperPath,
        int processId,
        string stagedPath,
        string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(helperPath),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helperPath)
        };
        startInfo.ArgumentList.Add(ApplyElevatedArgument);
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(Path.GetFullPath(stagedPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(targetPath));
        return startInfo;
    }

    private static void WaitForProcessToExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(milliseconds: 60_000))
            {
                throw new TimeoutException("The running application did not exit within 60 seconds.");
            }
        }
        catch (ArgumentException)
        {
            // The application already exited.
        }
    }

    internal static void ReplaceFileContentsWhenAvailable(string sourcePath, string targetPath)
    {
        RetryFileOperation(() =>
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.SequentialScan);
            using var target = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.SequentialScan);
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        });
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstHash = SHA256.HashData(first);
        var secondHash = SHA256.HashData(second);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static bool TryFilesMatch(string firstPath, string secondPath)
    {
        try
        {
            return FilesMatch(firstPath, secondPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteHelperWhenAvailable(string helperPath)
    {
        try
        {
            RetryFileOperation(() => File.Delete(helperPath));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RetryFileOperation(Action operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < FileOperationAttempts)
            {
                Thread.Sleep(FileOperationRetryDelay);
            }
        }
    }

    private static void StartApplication(string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("The application directory is unavailable.")
        };
        var helperPath = Environment.ProcessPath;
        if (helperPath is not null)
        {
            startInfo.ArgumentList.Add(CleanupArgument);
            startInfo.ArgumentList.Add(helperPath);
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The application did not restart.");
    }

    private static void TryRestartExistingApplication(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                StartApplication(targetPath);
            }
        }
        catch
        {
            // The error dialog still tells the user that the update failed.
        }
    }
}
