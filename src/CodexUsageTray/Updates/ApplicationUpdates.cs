using System.Security.Cryptography;

namespace CodexUsageTray;

internal enum ApplicationUpdateIntent
{
    Automatic,
    Manual
}

internal interface IApplicationUpdateSource
{
    Version CurrentVersion { get; }

    Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken cancellationToken);

    Task<StagedApplicationUpdate> DownloadAsync(
        AvailableApplicationUpdate update,
        CancellationToken cancellationToken);
}

internal interface IApplicationUpdateInstaller
{
    void Launch(StagedApplicationUpdate update);
}

internal interface IApplicationUpdateInteraction
{
    ValueTask PresentAsync(
        ApplicationUpdatePresentation presentation,
        CancellationToken cancellationToken);

    ValueTask<bool> ConfirmAsync(
        ApplicationUpdateOffer offer,
        CancellationToken cancellationToken);

    void ExitApplication();
}

internal sealed class ApplicationUpdates : IAsyncDisposable
{
    private readonly IApplicationUpdateSource source;
    private readonly IApplicationUpdateInstaller installer;
    private readonly IApplicationUpdateInteraction interaction;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateSync = new();
    private Task? activeRequest;
    private int disposed;

    internal ApplicationUpdates(
        IApplicationUpdateSource source,
        IApplicationUpdateInstaller installer,
        IApplicationUpdateInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(interaction);
        this.source = source;
        this.installer = installer;
        this.interaction = interaction;
    }

    internal static ApplicationUpdates CreateDefault(IApplicationUpdateInteraction interaction) =>
        new(
            GitHubApplicationUpdateSource.CreateDefault(),
            WindowsApplicationUpdateInstaller.Instance,
            interaction);

    public Task RequestAsync(
        ApplicationUpdateIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (intent is not ApplicationUpdateIntent.Automatic and not ApplicationUpdateIntent.Manual)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown Application Update intent.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource completion;
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (activeRequest is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activeRequest = completion.Task;
        }

        _ = CompleteRequestAsync(completion, intent, cancellationToken);
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        Task? request;
        lock (stateSync)
        {
            request = activeRequest;
        }

        try
        {
            if (request is not null)
            {
                try
                {
                    await request.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task CompleteRequestAsync(
        TaskCompletionSource completion,
        ApplicationUpdateIntent intent,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(intent, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task RunAsync(
        ApplicationUpdateIntent intent,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        StagedApplicationUpdate? staged = null;
        var handedOff = false;
        cancellation.Token.ThrowIfCancellationRequested();
        await interaction.PresentAsync(
            new ApplicationUpdatePresentation.Checking(),
            cancellation.Token).ConfigureAwait(false);
        try
        {
            AvailableApplicationUpdate? available;
            try
            {
                available = await source.CheckAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (intent == ApplicationUpdateIntent.Manual)
                {
                    await interaction.PresentAsync(
                        new ApplicationUpdatePresentation.Failed(exception.Message),
                        cancellation.Token).ConfigureAwait(false);
                }

                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (available is null)
            {
                if (intent == ApplicationUpdateIntent.Manual)
                {
                    await interaction.PresentAsync(
                        new ApplicationUpdatePresentation.Current(source.CurrentVersion),
                        cancellation.Token).ConfigureAwait(false);
                }

                return;
            }

            if (!await interaction.ConfirmAsync(
                    new ApplicationUpdateOffer(source.CurrentVersion, available.Version),
                    cancellation.Token)
                .ConfigureAwait(false))
            {
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await interaction.PresentAsync(
                new ApplicationUpdatePresentation.Downloading(available.Version),
                cancellation.Token).ConfigureAwait(false);
            try
            {
                staged = await source.DownloadAsync(available, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await interaction.PresentAsync(
                    new ApplicationUpdatePresentation.Failed(exception.Message),
                    cancellation.Token).ConfigureAwait(false);
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await interaction.PresentAsync(
                new ApplicationUpdatePresentation.Installing(staged.Version),
                cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                installer.Launch(staged);
            }
            catch (Exception exception)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await interaction.PresentAsync(
                    new ApplicationUpdatePresentation.Failed(exception.Message),
                    cancellation.Token).ConfigureAwait(false);
                return;
            }

            staged.TransferOwnership();
            handedOff = true;
            interaction.ExitApplication();
        }
        finally
        {
            staged?.Dispose();
            if (!handedOff && Volatile.Read(ref disposed) == 0)
            {
                await interaction.PresentAsync(
                    new ApplicationUpdatePresentation.Idle(),
                    lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private sealed class WindowsApplicationUpdateInstaller : IApplicationUpdateInstaller
    {
        public static WindowsApplicationUpdateInstaller Instance { get; } = new();

        public void Launch(StagedApplicationUpdate update)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The application executable path is unavailable.");
            UpdateInstaller.Launch(update, Environment.ProcessId, processPath);
        }
    }
}

internal abstract record ApplicationUpdatePresentation
{
    internal sealed record Idle : ApplicationUpdatePresentation;
    internal sealed record Checking : ApplicationUpdatePresentation;
    internal sealed record Downloading(Version Version) : ApplicationUpdatePresentation;
    internal sealed record Installing(Version Version) : ApplicationUpdatePresentation;
    internal sealed record Current(Version Version) : ApplicationUpdatePresentation;
    internal sealed record Failed(string Message) : ApplicationUpdatePresentation;
}

internal sealed record ApplicationUpdateOffer(Version CurrentVersion, Version AvailableVersion);

internal sealed record AvailableApplicationUpdate(
    Version Version,
    Uri ExecutableDownloadUrl);

internal sealed class StagedApplicationUpdate : IDisposable
{
    private int ownershipTransferred;

    internal StagedApplicationUpdate(Version version, string stagedPath, string expectedHash)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        Version = version;
        StagedPath = Path.GetFullPath(stagedPath);
        ExpectedHash = expectedHash;
    }

    internal Version Version { get; }
    internal string StagedPath { get; }
    internal string ExpectedHash { get; }

    internal void TransferOwnership() => Interlocked.Exchange(ref ownershipTransferred, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref ownershipTransferred, 1) == 0)
        {
            ApplicationUpdateFiles.TryDelete(StagedPath);
        }
    }
}

internal static class ApplicationUpdateFiles
{
    // Keep this handle open until the verified bytes have been copied or started.
    // FileShare.Read denies both writes and replacement of the verified file.
    internal static FileStream OpenVerifiedRead(string path, string expectedHash)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update executable failed its SHA-256 check.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
