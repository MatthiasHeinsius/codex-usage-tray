using System.Security.Cryptography;

namespace CodexUsageTray.Tests;

public sealed class ApplicationUpdatesTests
{
    private static readonly Version CurrentVersion = new(1, 2, 0);
    private static readonly Version AvailableVersion = new(1, 3, 0);

    [Fact]
    public async Task ManualRequestReportsCurrentVersionWhenNoUpdateIsAvailable()
    {
        var source = new ScriptedSource();
        var interaction = new RecordingInteraction();
        await using var updates = new ApplicationUpdates(source, new RecordingInstaller(), interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            interaction.Presentations,
            presentation => Assert.IsType<ApplicationUpdatePresentation.Checking>(presentation),
            presentation => Assert.Equal(
                CurrentVersion,
                Assert.IsType<ApplicationUpdatePresentation.Current>(presentation).Version),
            presentation => Assert.IsType<ApplicationUpdatePresentation.Idle>(presentation));
    }

    [Fact]
    public async Task AutomaticRequestStaysSilentWhenNoUpdateIsAvailable()
    {
        var interaction = new RecordingInteraction();
        await using var updates = new ApplicationUpdates(
            new ScriptedSource(),
            new RecordingInstaller(),
            interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Automatic,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            interaction.Presentations,
            presentation => Assert.IsType<ApplicationUpdatePresentation.Checking>(presentation),
            presentation => Assert.IsType<ApplicationUpdatePresentation.Idle>(presentation));
    }

    [Fact]
    public async Task DeclinedUpdateIsNotDownloadedOrInstalled()
    {
        var source = new ScriptedSource { AvailableUpdate = CreateAvailableUpdate() };
        var installer = new RecordingInstaller();
        var interaction = new RecordingInteraction { Confirmation = false };
        await using var updates = new ApplicationUpdates(source, installer, interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, interaction.ConfirmationRequests);
        Assert.Equal(0, source.DownloadRequests);
        Assert.Null(installer.Update);
        Assert.IsType<ApplicationUpdatePresentation.Idle>(interaction.Presentations[^1]);
    }

    [Fact]
    public async Task AcceptedUpdateTransfersOwnershipAfterInstallerStartsAndRequestsExit()
    {
        using var directory = new TemporaryDirectory("application-update-success");
        var stagedPath = directory.FilePath("staged.exe");
        File.WriteAllText(stagedPath, "verified update");
        var source = new ScriptedSource
        {
            AvailableUpdate = CreateAvailableUpdate(),
            Download = (_, _) => Task.FromResult(
                new StagedApplicationUpdate(AvailableVersion, stagedPath,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stagedPath)))))
        };
        var installer = new RecordingInstaller();
        var interaction = new RecordingInteraction { Confirmation = true };
        await using var updates = new ApplicationUpdates(source, installer, interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        Assert.NotNull(installer.Update);
        Assert.Equal(stagedPath, installer.Update.StagedPath);
        Assert.True(File.Exists(stagedPath));
        Assert.Equal(1, interaction.ExitRequests);
        Assert.Collection(
            interaction.Presentations,
            presentation => Assert.IsType<ApplicationUpdatePresentation.Checking>(presentation),
            presentation => Assert.Equal(
                AvailableVersion,
                Assert.IsType<ApplicationUpdatePresentation.Downloading>(presentation).Version),
            presentation => Assert.Equal(
                AvailableVersion,
                Assert.IsType<ApplicationUpdatePresentation.Installing>(presentation).Version));
    }

    [Fact]
    public async Task CancellationDuringSuccessfulInstallerLaunchStillRequestsExit()
    {
        using var directory = new TemporaryDirectory("application-update-handoff-cancellation");
        var stagedPath = directory.FilePath("staged.exe");
        File.WriteAllText(stagedPath, "verified update");
        using var cancellation = new CancellationTokenSource();
        var source = new ScriptedSource
        {
            AvailableUpdate = CreateAvailableUpdate(),
            Download = (_, _) => Task.FromResult(
                new StagedApplicationUpdate(AvailableVersion, stagedPath,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stagedPath)))))
        };
        var installer = new RecordingInstaller { OnLaunch = cancellation.Cancel };
        var interaction = new RecordingInteraction { Confirmation = true };
        await using var updates = new ApplicationUpdates(source, installer, interaction);

        await updates.RequestAsync(ApplicationUpdateIntent.Manual, cancellation.Token);

        Assert.NotNull(installer.Update);
        Assert.True(File.Exists(stagedPath));
        Assert.Equal(1, interaction.ExitRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeConsentUsesIntentVisibilityPolicy(bool manual)
    {
        var source = new ScriptedSource
        {
            Check = _ => Task.FromException<AvailableApplicationUpdate?>(new IOException("check failed"))
        };
        var interaction = new RecordingInteraction();
        await using var updates = new ApplicationUpdates(source, new RecordingInstaller(), interaction);

        await updates.RequestAsync(
            manual ? ApplicationUpdateIntent.Manual : ApplicationUpdateIntent.Automatic,
            TestContext.Current.CancellationToken);

        var failure = interaction.Presentations.OfType<ApplicationUpdatePresentation.Failed>().SingleOrDefault();
        if (manual)
        {
            Assert.NotNull(failure);
            Assert.Equal("check failed", failure.Message);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.IsType<ApplicationUpdatePresentation.Idle>(interaction.Presentations[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterConsentIsReported(bool timeout)
    {
        var source = new ScriptedSource
        {
            AvailableUpdate = CreateAvailableUpdate(),
            Download = (_, _) =>
                Task.FromException<StagedApplicationUpdate>(timeout
                    ? new TimeoutException("download failed")
                    : new InvalidDataException("download failed"))
        };
        var interaction = new RecordingInteraction { Confirmation = true };
        var installer = new RecordingInstaller();
        await using var updates = new ApplicationUpdates(source, installer, interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Automatic,
            TestContext.Current.CancellationToken);

        var failure = Assert.Single(
            interaction.Presentations.OfType<ApplicationUpdatePresentation.Failed>());
        Assert.Equal("download failed", failure.Message);
        Assert.Null(installer.Update);
        Assert.IsType<ApplicationUpdatePresentation.Idle>(interaction.Presentations[^1]);
    }

    [Fact]
    public async Task SourceTimeoutIsAnOperationalFailureWhenTheRequestWasNotCanceled()
    {
        var source = new ScriptedSource
        {
            Check = _ => Task.FromException<AvailableApplicationUpdate?>(
                new OperationCanceledException("source timeout"))
        };
        var interaction = new RecordingInteraction();
        await using var updates = new ApplicationUpdates(source, new RecordingInstaller(), interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "source timeout",
            Assert.Single(interaction.Presentations.OfType<ApplicationUpdatePresentation.Failed>()).Message);
        Assert.IsType<ApplicationUpdatePresentation.Idle>(interaction.Presentations[^1]);
    }

    [Fact]
    public async Task InstallerFailureDeletesTheStagedUpdate()
    {
        using var directory = new TemporaryDirectory("application-update-installer-failure");
        var stagedPath = directory.FilePath("staged.exe");
        File.WriteAllText(stagedPath, "verified update");
        var source = new ScriptedSource
        {
            AvailableUpdate = CreateAvailableUpdate(),
            Download = (_, _) => Task.FromResult(
                new StagedApplicationUpdate(AvailableVersion, stagedPath,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stagedPath)))))
        };
        var installer = new RecordingInstaller
        {
            Failure = new InvalidOperationException("installer failed")
        };
        var interaction = new RecordingInteraction { Confirmation = true };
        await using var updates = new ApplicationUpdates(source, installer, interaction);

        await updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(stagedPath));
        Assert.Equal(0, interaction.ExitRequests);
        Assert.Equal(
            "installer failed",
            Assert.Single(interaction.Presentations.OfType<ApplicationUpdatePresentation.Failed>()).Message);
    }

    [Fact]
    public async Task CancellationAfterStagingDeletesTheStagedUpdate()
    {
        using var directory = new TemporaryDirectory("application-update-cancellation");
        var stagedPath = directory.FilePath("staged.exe");
        File.WriteAllText(stagedPath, "verified update");
        using var cancellation = new CancellationTokenSource();
        var source = new ScriptedSource
        {
            AvailableUpdate = CreateAvailableUpdate(),
            Download = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new StagedApplicationUpdate(AvailableVersion, stagedPath,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(stagedPath)))));
            }
        };
        var interaction = new RecordingInteraction { Confirmation = true };
        await using var updates = new ApplicationUpdates(source, new RecordingInstaller(), interaction);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updates.RequestAsync(ApplicationUpdateIntent.Manual, cancellation.Token));

        Assert.False(File.Exists(stagedPath));
        Assert.IsType<ApplicationUpdatePresentation.Idle>(interaction.Presentations[^1]);
    }

    [Fact]
    public async Task CallerCancellationWhileCheckingStopsBeforeStaging()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ScriptedSource
        {
            Check = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CreateAvailableUpdate();
            }
        };
        var installer = new RecordingInstaller();
        var interaction = new RecordingInteraction();
        await using var updates = new ApplicationUpdates(source, installer, interaction);
        var request = updates.RequestAsync(ApplicationUpdateIntent.Manual, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, source.DownloadRequests);
        Assert.Null(installer.Update);
        Assert.IsType<ApplicationUpdatePresentation.Idle>(interaction.Presentations[^1]);
    }

    [Fact]
    public async Task SecondRequestIsIgnoredWhileTheFirstIsActive()
    {
        var release = new TaskCompletionSource<AvailableApplicationUpdate?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource { Check = _ => release.Task };
        await using var updates = new ApplicationUpdates(
            source,
            new RecordingInstaller(),
            new RecordingInteraction());

        var first = updates.RequestAsync(
            ApplicationUpdateIntent.Automatic,
            TestContext.Current.CancellationToken);
        await updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, source.CheckRequests);
        release.SetResult(null);
        await first;
    }

    [Fact]
    public async Task DisposalCancelsAndWaitsForTheActiveRequest()
    {
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource
        {
            Check = async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.SetResult();
                    throw;
                }
            }
        };
        var interaction = new RecordingInteraction();
        var updates = new ApplicationUpdates(source, new RecordingInstaller(), interaction);
        var request = updates.RequestAsync(
            ApplicationUpdateIntent.Automatic,
            TestContext.Current.CancellationToken);

        await updates.DisposeAsync();
        await cancellationObserved.Task;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.DoesNotContain(
            interaction.Presentations,
            presentation => presentation is ApplicationUpdatePresentation.Idle);
    }

    [Fact]
    public async Task DisposalSuppressesPresentationsAfterACancellationIgnoringCheckReturns()
    {
        var release = new TaskCompletionSource<AvailableApplicationUpdate?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource { Check = _ => release.Task };
        var interaction = new RecordingInteraction();
        var updates = new ApplicationUpdates(source, new RecordingInstaller(), interaction);
        var request = updates.RequestAsync(
            ApplicationUpdateIntent.Manual,
            TestContext.Current.CancellationToken);

        var disposal = updates.DisposeAsync().AsTask();
        release.SetResult(null);
        await disposal;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Collection(
            interaction.Presentations,
            presentation => Assert.IsType<ApplicationUpdatePresentation.Checking>(presentation));
    }

    private static AvailableApplicationUpdate CreateAvailableUpdate() => new(
        AvailableVersion,
        new Uri("https://example.test/CodexUsageTray.exe"),
        new Uri("https://example.test/SHA256SUMS.txt"));

    private sealed class ScriptedSource : IApplicationUpdateSource
    {
        public Version CurrentVersion => ApplicationUpdatesTests.CurrentVersion;
        public AvailableApplicationUpdate? AvailableUpdate { get; init; }
        public Func<CancellationToken, Task<AvailableApplicationUpdate?>>? Check { get; init; }
        public Func<AvailableApplicationUpdate, CancellationToken, Task<StagedApplicationUpdate>>? Download
        {
            get;
            init;
        }

        public int CheckRequests { get; private set; }
        public int DownloadRequests { get; private set; }

        public Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckRequests++;
            return Check?.Invoke(cancellationToken) ?? Task.FromResult(AvailableUpdate);
        }

        public Task<StagedApplicationUpdate> DownloadAsync(
            AvailableApplicationUpdate update,
            CancellationToken cancellationToken)
        {
            DownloadRequests++;
            return Download?.Invoke(update, cancellationToken)
                ?? throw new InvalidOperationException("No scripted download was configured.");
        }
    }

    private sealed class RecordingInstaller : IApplicationUpdateInstaller
    {
        public StagedApplicationUpdate? Update { get; private set; }
        public Exception? Failure { get; init; }
        public Action? OnLaunch { get; init; }

        public void Launch(StagedApplicationUpdate update)
        {
            Update = update;
            OnLaunch?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingInteraction : IApplicationUpdateInteraction
    {
        public List<ApplicationUpdatePresentation> Presentations { get; } = [];
        public bool Confirmation { get; init; }
        public int ConfirmationRequests { get; private set; }
        public int ExitRequests { get; private set; }

        public ValueTask PresentAsync(
            ApplicationUpdatePresentation presentation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Presentations.Add(presentation);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ConfirmAsync(
            ApplicationUpdateOffer offer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmationRequests++;
            Assert.Equal(CurrentVersion, offer.CurrentVersion);
            Assert.Equal(AvailableVersion, offer.AvailableVersion);
            return ValueTask.FromResult(Confirmation);
        }

        public void ExitApplication() => ExitRequests++;
    }
}
