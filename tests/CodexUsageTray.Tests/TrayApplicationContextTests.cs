using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class TrayApplicationContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StartupRefreshesUsageAndRespectsTheAutomaticUpdatePreference(bool automaticUpdates) =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var application = new TestApplication();
            application.Settings.ActivationEnabled = true;
            var usagePresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var label in application.Popup.Controls.OfType<Label>())
            {
                label.TextChanged += (_, _) =>
                {
                    if (label.Text == "75% left")
                    {
                        usagePresented.TrySetResult();
                    }
                };
            }
            application.Start(automaticUpdates);
            await usagePresented.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            var state = await application.Shell!.CaptureStateAsync(cancellationToken);

            Assert.Equal([UsageObservationRequest.AllowanceWindows], application.Observations.Requests);
            Assert.Equal("Codex · 5h 75%", state.TrayTooltip);
            Assert.True(state.AllowanceActivationEnabled);
            Assert.False(state.AllowanceNotificationsEnabled);
            Assert.Equal(automaticUpdates, state.AutomaticUpdateEnabled);
            Assert.Equal(automaticUpdates ? 1 : 0, application.Source.CheckCount);
            Assert.True(state.UpdateEnabled);
        });

    [Fact]
    public Task InitializationFailureDisposesTheUsageServiceAndShell() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var application = new TestApplication();
            var failure = new IOException("Synthetic update initialization failure.");

            Assert.Same(failure, Assert.Throws<IOException>(() =>
                application.Start(automaticUpdates: true, _ => throw failure)));

            Assert.True(application.Popup.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => application.Shell!.Present(UsagePresentation.CreateInitial()));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                application.Usage!.RequestAsync(UsageUpdateIntent.Routine, cancellationToken));
            Assert.Empty(application.Observations.Requests);
            Assert.Equal(0, application.Source.CheckCount);
        });

    [Fact]
    public Task ScheduledRefreshDoesNotQueueBehindAnActiveRequest() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var application = new TestApplication();
            application.Observations.Block = true;
            application.Start(automaticUpdates: false);
            await application.Observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            await application.Context!.RequestScheduledAsync();

            Assert.Equal([UsageObservationRequest.AllowanceWindows], application.Observations.Requests);
        });

    [Fact]
    public Task ExitCancelsActiveUsageAndApplicationUpdatesBeforeDisposingTheShell() =>
        StaTest.RunAsync(async cancellationToken =>
        {
            using var application = new TestApplication();
            application.Observations.Block = true;
            application.Source.Block = true;
            application.Start(automaticUpdates: true);
            await Task.WhenAll(application.Observations.Started.Task, application.Source.Started.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var canceledBeforeShellDisposal = false;
            application.Popup.Disposed += (_, _) => canceledBeforeShellDisposal =
                application.Observations.CancellationObserved && application.Source.CancellationObserved;

            application.Exit();

            Assert.True(canceledBeforeShellDisposal);
            Assert.True(application.Popup.IsDisposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                application.Usage!.RequestAsync(UsageUpdateIntent.Routine, cancellationToken));
        });

    private sealed class TestApplication : IDisposable
    {
        private readonly TestRegistryKey registry = new();
        private TrayApplicationContext? context;

        public TestApplication() => Settings = new RegistryApplicationSettings(registry.Path);

        public RegistryApplicationSettings Settings { get; }
        public RecordingObservations Observations { get; } = new();
        public RecordingUpdateSource Source { get; } = new();
        public UsagePopupForm Popup { get; } = new(initialCompactView: false) { Opacity = 0 };
        public WinFormsApplicationShell? Shell { get; private set; }
        public UsageUpdates? Usage { get; private set; }
        public TrayApplicationContext? Context => context;

        public void Start(
            bool automaticUpdates,
            Func<IApplicationUpdateInteraction, ApplicationUpdates>? createApplicationUpdates = null) =>
            context = new TrayApplicationContext(
                automaticUpdates,
                _ => Usage = new UsageUpdates(
                    Observations,
                    new CodexWindowStarter(new ScriptedCodexProcessExecution()),
                    Settings,
                    TimeProvider.System,
                    CultureInfo.InvariantCulture),
                createApplicationUpdates ?? (interaction => new ApplicationUpdates(Source, new RejectingInstaller(), interaction)),
                commands => Shell = new WinFormsApplicationShell(
                    false, automaticUpdates, commands, popupForm: Popup, trayIconId: Guid.NewGuid()));

        public void Exit()
        {
            var exitingContext = context;
            context = null;
            exitingContext?.ExitThread();
            exitingContext?.Dispose();
        }

        public void Dispose()
        {
            Exit();
            Popup.Dispose();
            registry.Dispose();
        }
    }

    private sealed class RecordingObservations : IUsageObservationReader
    {
        public List<UsageObservationRequest> Requests { get; } = [];
        public bool Block { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UsageObservations> ReadAsync(UsageObservationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Started.TrySetResult();
            if (Block)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CancellationObserved = cancellationToken.IsCancellationRequested;
                }
            }
            var now = DateTimeOffset.Now;
            return new UsageObservations(
                new AccountUsageObservation(now,
                    [new AllowanceWindow(25, TimeSpan.FromHours(5), now.AddHours(5))],
                    "plus", "Codex", new AccountActivityObservation.NotRequested()), Local: null);
        }
    }

    private sealed class RecordingUpdateSource : IApplicationUpdateSource
    {
        public Version CurrentVersion => new(1, 0, 0);
        public int CheckCount { get; private set; }
        public bool Block { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            Started.TrySetResult();
            if (Block)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CancellationObserved = cancellationToken.IsCancellationRequested;
                }
            }
            return null;
        }

        public Task<StagedApplicationUpdate> DownloadAsync(AvailableApplicationUpdate update, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No update should be downloaded.");
    }

    private sealed class RejectingInstaller : IApplicationUpdateInstaller
    {
        public void Launch(StagedApplicationUpdate update) =>
            throw new InvalidOperationException("No update should be installed.");
    }
}
