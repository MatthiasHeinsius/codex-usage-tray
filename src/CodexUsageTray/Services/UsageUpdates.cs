using System.Globalization;

namespace CodexUsageTray;

internal enum UsageUpdateIntent
{
    Routine,
    Activity
}

internal interface IUsageUpdates : IAsyncDisposable
{
    bool ActivationEnabled { get; set; }
    bool NotificationsEnabled { get; set; }
    Task<UsagePresentation.Ready> RequestAsync(
        UsageUpdateIntent intent,
        CancellationToken cancellationToken = default);
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

    public static UsageUpdates CreateDefault(ICodexAuthenticationInteraction authenticationInteraction)
    {
        ArgumentNullException.ThrowIfNull(authenticationInteraction);
        var processExecution = new WindowsCodexProcessExecution();
        return new UsageUpdates(
            new CodexUsageObservationReader(processExecution, authenticationInteraction),
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

    public Task<UsagePresentation.Ready> RequestAsync(
        UsageUpdateIntent intent,
        CancellationToken cancellationToken = default)
    {
        var observationRequest = intent switch
        {
            UsageUpdateIntent.Routine => UsageObservationRequest.AllowanceWindows,
            UsageUpdateIntent.Activity => UsageObservationRequest.AllowanceWindowsAndActivity,
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent,
                "Unknown Usage Update intent.")
        };
        return ExecuteUpdateAsync(observationRequest, CapturePreferences(), cancellationToken);
    }

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

    private async Task<UsagePresentation.Ready> ExecuteUpdateAsync(
        UsageObservationRequest observationRequest,
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
            var snapshot = await RequestSnapshotAsync(observationRequest, cancellation.Token)
                .ConfigureAwait(false);
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
