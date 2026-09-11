using System.Globalization;

namespace CodexUsageTray;

internal sealed class UsageUpdates : IAsyncDisposable
{
    private readonly UsageSnapshots snapshots;
    private readonly AllowanceWindowActivation activation;
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
        snapshots = new UsageSnapshots(observations);
        activation = new AllowanceWindowActivation(
            activationCommand,
            snapshots,
            settings,
            timeProvider);
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.formatProvider = formatProvider;
        activationEnabled = settings.ActivationEnabled;
        notificationsEnabled = settings.NotificationsEnabled;
    }

    public static UsageUpdates CreateDefault() => new(
        new CodexUsageObservationReader(),
        new CodexWindowStarter(),
        new RegistryAllowanceWindowActivationSettings(),
        TimeProvider.System,
        CultureInfo.CurrentCulture);

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

    public Task<UsagePresentation> RefreshAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(includeActivity: false, CapturePreferences(), cancellationToken);

    public Task<UsagePresentation> RefreshWithActivityAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(includeActivity: true, CapturePreferences(), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        await snapshots.DisposeAsync().ConfigureAwait(false);
        await updateGate.WaitAsync().ConfigureAwait(false);
        activation.Dispose();
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

    private async Task<UsagePresentation> RequestAsync(
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
            var snapshot = includeActivity
                ? await snapshots.RefreshWithActivityAsync(cancellation.Token).ConfigureAwait(false)
                : await snapshots.RefreshAsync(cancellation.Token).ConfigureAwait(false);
            var allowanceEvents = await activation
                .ObserveAsync(preferences.ActivationEnabled, snapshot, cancellation.Token)
                .ConfigureAwait(false);
            var finalSnapshot = snapshots.Current ?? snapshot;
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
