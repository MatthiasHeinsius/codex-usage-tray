using System.Globalization;

namespace CodexUsageTray;

internal interface IUsageUpdates : IAsyncDisposable
{
    bool ActivationEnabled { get; set; }
    bool NotificationsEnabled { get; set; }
    Task<UsagePresentation.Ready> RefreshAsync(CancellationToken cancellationToken = default);
    Task<UsagePresentation.Ready> RefreshWithActivityAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class UsageUpdates : IUsageUpdates
{
    private readonly IUsageObservationReader observations;
    private readonly IAllowanceWindowActivationCommand activationCommand;
    private readonly IAllowanceWindowActivationSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly IFormatProvider formatProvider;
    private readonly object preferencesSync = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool activationEnabled;
    private bool notificationsEnabled;
    private int disposed;

    internal UsageUpdates(
        IUsageObservationReader observations,
        IAllowanceWindowActivationCommand activationCommand,
        IAllowanceWindowActivationSettings settings,
        TimeProvider timeProvider,
        IFormatProvider formatProvider)
    {
        this.observations = observations;
        this.activationCommand = activationCommand;
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.formatProvider = formatProvider;
        activationEnabled = settings.ActivationEnabled;
        notificationsEnabled = settings.NotificationsEnabled;
    }

    public static UsageUpdates CreateDefault()
    {
        var processExecution = new WindowsCodexProcessExecution();
        return new UsageUpdates(
            new CodexUsageObservationReader(processExecution),
            new CodexWindowStarter(processExecution),
            new RegistryAllowanceWindowActivationSettings(),
            TimeProvider.System,
            CultureInfo.CurrentCulture);
    }

    public bool ActivationEnabled
    {
        get
        {
            lock (preferencesSync)
            {
                return activationEnabled;
            }
        }
        set
        {
            lock (preferencesSync)
            {
                settings.ActivationEnabled = value;
                activationEnabled = value;
            }
        }
    }

    public bool NotificationsEnabled
    {
        get
        {
            lock (preferencesSync)
            {
                return notificationsEnabled;
            }
        }
        set
        {
            lock (preferencesSync)
            {
                settings.NotificationsEnabled = value;
                notificationsEnabled = value;
            }
        }
    }

    public Task<UsagePresentation.Ready> RefreshAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(includeActivity: false, CapturePreferences(), cancellationToken);

    public Task<UsagePresentation.Ready> RefreshWithActivityAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(includeActivity: true, CapturePreferences(), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        var runningRefresh = CancelActiveRefresh();
        if (runningRefresh is not null)
        {
            try
            {
                await runningRefresh.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposal owns cancellation of the shared observation read.
            }
        }

        await updateGate.WaitAsync().ConfigureAwait(false);
        updateGate.Dispose();
        lifetime.Dispose();
    }

    private (bool ActivationEnabled, bool NotificationsEnabled) CapturePreferences()
    {
        lock (preferencesSync)
        {
            return (activationEnabled, notificationsEnabled);
        }
    }

    private async Task<UsagePresentation.Ready> RequestAsync(
        bool includeActivity,
        (bool ActivationEnabled, bool NotificationsEnabled) preferences,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        await updateGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var snapshot = await RefreshSnapshotAsync(includeActivity, cancellation.Token).ConfigureAwait(false);
            var (finalSnapshot, allowanceEvents) = await ObserveAllowanceWindowsAsync(
                    preferences.ActivationEnabled,
                    snapshot,
                    cancellation.Token)
                .ConfigureAwait(false);
            return UsagePresentation.Create(
                finalSnapshot,
                allowanceEvents,
                preferences.NotificationsEnabled,
                timeProvider.GetLocalNow(),
                formatProvider);
        }
        finally
        {
            updateGate.Release();
        }
    }
}
