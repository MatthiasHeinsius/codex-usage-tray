using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace CodexUsageTray;

internal enum UpdateInstallerResult
{
    Succeeded = 0,
    RecoverableFailure = 1,
    UnrecoverableFailure = 2
}

internal static class UpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string ApplyElevatedArgument = "--apply-update-elevated";
    private const string CleanupArgument = "--cleanup-update";
    private const int FileOperationAttempts = 50;
    private const int SharingViolationError = 32;
    private const int LockViolationError = 33;
    private const int UnableToRemoveReplacedError = 1175;
    private static readonly TimeSpan FileOperationRetryDelay = TimeSpan.FromMilliseconds(100);

    internal static bool TryHandleCommandLine(string[] args, out int exitCode) =>
        TryHandleCommandLine(args, out exitCode, WindowsUpdateInstallerInteraction.Instance);

    internal static bool TryHandleCommandLine(
        string[] args,
        out int exitCode,
        IUpdateInstallerInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        if (args is [ApplyArgument or ApplyElevatedArgument, var processIdText, var stagedPath, var targetPath, var expectedHash]
            && int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            var elevated = string.Equals(args[0], ApplyElevatedArgument, StringComparison.Ordinal);
            exitCode = (int)Apply(
                processId,
                stagedPath,
                targetPath,
                expectedHash,
                allowElevation: !elevated,
                restartApplication: !elevated,
                interaction);
            return true;
        }

        if (args is [CleanupArgument, var helperPath])
        {
            DeleteHelperWhenAvailable(helperPath);
        }

        exitCode = 0;
        return false;
    }

    internal static void Launch(StagedApplicationUpdate update, int processId, string targetPath) =>
        Launch(update, processId, targetPath, WindowsUpdateInstallerInteraction.Instance);

    internal static void Launch(
        StagedApplicationUpdate update,
        int processId,
        string targetPath,
        IUpdateInstallerInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(interaction);
        var helperPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexUsageTray-update-helper-{Guid.NewGuid():N}.exe");
        try
        {
            using var staged = ApplicationUpdateFiles.OpenVerifiedRead(update.StagedPath, update.ExpectedHash);
            CopyFileContents(staged, helperPath);
            using var helper = ApplicationUpdateFiles.OpenVerifiedRead(helperPath, update.ExpectedHash);
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
            startInfo.ArgumentList.Add(update.ExpectedHash);
            interaction.StartInstaller(startInfo);
        }
        catch
        {
            ApplicationUpdateFiles.TryDelete(helperPath);
            throw;
        }
    }

    private static UpdateInstallerResult Apply(
        int processId,
        string stagedPath,
        string targetPath,
        string expectedHash,
        bool allowElevation,
        bool restartApplication,
        IUpdateInstallerInteraction interaction)
    {
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
            ?? throw new InvalidOperationException("The application directory is unavailable.");
        var updateId = Guid.NewGuid().ToString("N");
        var preparedPath = Path.Combine(targetDirectory, $".CodexUsageTray-update-{updateId}.tmp");
        var backupPath = Path.Combine(targetDirectory, $".CodexUsageTray-backup-{updateId}.exe");
        var applicationExited = false;
        var backupReady = false;
        try
        {
            WaitForProcessToExit(processId);
            applicationExited = true;
            if (RequiresElevation(targetPath))
            {
                if (allowElevation)
                {
                    return ApplyWithElevation(processId, stagedPath, targetPath, expectedHash, interaction);
                }

                throw new UnauthorizedAccessException(
                    "Administrator access was granted, but the installed executable is still read-only.");
            }

            try
            {
                using (var staged = ApplicationUpdateFiles.OpenVerifiedRead(stagedPath, expectedHash))
                {
                    CopyFileContents(staged, preparedPath);
                }

                try
                {
                    ReplaceFileWhenAvailable(preparedPath, targetPath, backupPath);
                    backupReady = true;
                }
                catch
                {
                    // ReplaceFileW can move the old target to the backup path before reporting failure.
                    backupReady = File.Exists(backupPath);
                    throw;
                }
            }
            catch (UnauthorizedAccessException) when (allowElevation && !backupReady)
            {
                TryDeleteFileWhenAvailable(preparedPath);
                return ApplyWithElevation(processId, stagedPath, targetPath, expectedHash, interaction);
            }

            using (var installed = ApplicationUpdateFiles.OpenVerifiedRead(targetPath, expectedHash))
            {
                if (restartApplication)
                {
                    interaction.StartApplication(targetPath);
                }
            }

            ApplicationUpdateFiles.TryDelete(stagedPath);
            TryDeleteFileWhenAvailable(preparedPath);
            TryDeleteFileWhenAvailable(backupPath);
            return UpdateInstallerResult.Succeeded;
        }
        catch (Exception exception)
        {
            var failure = exception;
            var canRestart = applicationExited;
            if (backupReady)
            {
                try
                {
                    RestoreBackupWhenAvailable(backupPath, targetPath);
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

            ApplicationUpdateFiles.TryDelete(stagedPath);
            TryDeleteFileWhenAvailable(preparedPath);
            if (canRestart)
            {
                TryDeleteFileWhenAvailable(backupPath);
            }

            interaction.ShowFailure($"The update could not be installed.\n\n{failure.Message}");
            if (restartApplication && canRestart)
            {
                TryRestartExistingApplication(targetPath, interaction);
            }

            return canRestart
                ? UpdateInstallerResult.RecoverableFailure
                : UpdateInstallerResult.UnrecoverableFailure;
        }
    }

    private static bool RequiresElevation(string targetPath)
    {
        try
        {
            using var stream = new FileStream(
                targetPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            // A sharing violation does not prevent an atomic replacement when the owner allows rename.
            return false;
        }
    }

    private static UpdateInstallerResult ApplyWithElevation(
        int processId,
        string stagedPath,
        string targetPath,
        string expectedHash,
        IUpdateInstallerInteraction interaction)
    {
        var helperPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The update helper path is unavailable.");
        var startInfo = CreateElevatedInstallerStartInfo(
            helperPath,
            processId,
            stagedPath,
            targetPath,
            expectedHash);
        UpdateInstallerResult exitCode;
        using (var helper = ApplicationUpdateFiles.OpenVerifiedRead(helperPath, expectedHash))
        {
            exitCode = interaction.RunElevatedInstaller(startInfo);
        }

        if (exitCode == UpdateInstallerResult.Succeeded)
        {
            try
            {
                using var installed = ApplicationUpdateFiles.OpenVerifiedRead(targetPath, expectedHash);
                interaction.StartApplication(targetPath);
            }
            catch (Exception exception)
            {
                interaction.ShowFailure($"The updated application could not be started.\n\n{exception.Message}");
                return UpdateInstallerResult.UnrecoverableFailure;
            }
        }
        else if (exitCode == UpdateInstallerResult.RecoverableFailure)
        {
            TryRestartExistingApplication(targetPath, interaction);
        }

        return exitCode;
    }

    private static ProcessStartInfo CreateElevatedInstallerStartInfo(
        string helperPath,
        int processId,
        string stagedPath,
        string targetPath,
        string expectedHash)
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
        startInfo.ArgumentList.Add(expectedHash);
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

    private static void CopyFileContents(Stream source, string targetPath)
    {
        using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.SequentialScan);
        source.CopyTo(target);
        target.Flush(flushToDisk: true);
    }

    private static void ReplaceFileWhenAvailable(
        string sourcePath,
        string targetPath,
        string? destinationBackupPath) =>
        RetryFileOperation(() => File.Replace(
            sourcePath,
            targetPath,
            destinationBackupPath,
            ignoreMetadataErrors: false));

    private static void RestoreBackupWhenAvailable(string backupPath, string targetPath)
    {
        RetryFileOperation(() =>
        {
            if (File.Exists(targetPath))
            {
                File.Replace(
                    backupPath,
                    targetPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: false);
                return;
            }

            File.Move(backupPath, targetPath);
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
        TryDeleteFileWhenAvailable(helperPath);
    }

    private static void TryDeleteFileWhenAvailable(string path)
    {
        try
        {
            RetryFileOperation(() => File.Delete(path), retryAccessDenied: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RetryFileOperation(Action operation, bool retryAccessDenied = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException exception) when (
                attempt < FileOperationAttempts
                && IsRetryableFileError(exception))
            {
                Thread.Sleep(FileOperationRetryDelay);
            }
            catch (UnauthorizedAccessException) when (
                attempt < FileOperationAttempts
                && retryAccessDenied)
            {
                Thread.Sleep(FileOperationRetryDelay);
            }
        }
    }

    private static bool IsRetryableFileError(IOException exception) =>
        (exception.HResult & 0xffff) is
            SharingViolationError
            or LockViolationError
            or UnableToRemoveReplacedError;

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

    private static void TryRestartExistingApplication(
        string targetPath,
        IUpdateInstallerInteraction interaction)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                interaction.StartApplication(targetPath);
            }
        }
        catch
        {
            // The error dialog still tells the user that the update failed.
        }
    }

    private sealed class WindowsUpdateInstallerInteraction : IUpdateInstallerInteraction
    {
        public static WindowsUpdateInstallerInteraction Instance { get; } = new();

        public void StartInstaller(ProcessStartInfo startInfo)
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The update installer did not start.");
        }

        public UpdateInstallerResult RunElevatedInstaller(ProcessStartInfo startInfo)
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated update installer did not start.");
            process.WaitForExit();
            return (UpdateInstallerResult)process.ExitCode;
        }

        public void StartApplication(string targetPath) => UpdateInstaller.StartApplication(targetPath);

        public void ShowFailure(string message) => MessageBox.Show(
            message,
            "Codex usage update",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

internal interface IUpdateInstallerInteraction
{
    void StartInstaller(ProcessStartInfo startInfo);
    UpdateInstallerResult RunElevatedInstaller(ProcessStartInfo startInfo);
    void StartApplication(string targetPath);
    void ShowFailure(string message);
}
