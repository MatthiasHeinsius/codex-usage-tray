using System.Globalization;

namespace CodexUsageTray;

internal enum UsageUpdateIntent
{
    Routine,
    Activity,
    Reconnect
}

internal interface IUsageUpdates : IAsyncDisposable
{
    bool ActivationEnabled { get; set; }
    bool NotificationsEnabled { get; set; }

    // Cancellation stops this request's observation; later requests start fresh.
    // Active requests retain the gate until their adapter cleanup finishes.
    Task<UsagePresentation.Ready> RequestAsync(
        UsageUpdateIntent intent,
        CancellationToken cancellationToken = default);
}

internal sealed partial class UsageUpdates : IUsageUpdates
{
    private readonly IUsageObservationReader observations;
    private WindowsCodexProcessExecution? ownedProcessExecution;
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
    private string? lastPresentedAccountEmail;
    private UsagePresentation.ActivityIndicatorPresentation lastPresentedAllowance =
        UsagePresentation.ActivityIndicatorPresentation.Unavailable;
    private DateTimeOffset? awaitingActivationResetAt;

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
        var updates = new UsageUpdates(
            new CodexUsageObservationReader(processExecution, authenticationInteraction),
            new CodexWindowStarter(processExecution),
            RegistryApplicationSettings.Current,
            TimeProvider.System,
            CultureInfo.CurrentCulture);
        updates.ownedProcessExecution = processExecution;
        return updates;
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
            UsageUpdateIntent.Activity or UsageUpdateIntent.Reconnect =>
                UsageObservationRequest.AllowanceWindowsAndActivity,
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent,
                "Unknown Usage Update intent.")
        };
        return ExecuteUpdateAsync(
            observationRequest,
            CapturePreferences(),
            intent == UsageUpdateIntent.Reconnect,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        await updateGate.WaitAsync().ConfigureAwait(false);
        if (ownedProcessExecution is not null)
        {
            await ownedProcessExecution.DisposeAsync().ConfigureAwait(false);
        }
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
        bool reconnect,
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
            if (reconnect)
            {
                ownedProcessExecution?.RequestReconnect();
            }
            var snapshot = await RequestSnapshotAsync(observationRequest, cancellation.Token)
                .ConfigureAwait(false);
            var (finalSnapshot, allowanceEvents) = await ObserveAllowanceWindowsAsync(
                    preferences.ActivationEnabled,
                    snapshot,
                    cancellation.Token)
                .ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return CreatePresentation(
                finalSnapshot,
                allowanceEvents,
                preferences.NotificationsEnabled,
                cancellation.Token);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private UsagePresentation.Ready CreatePresentation(
        UsageSnapshot snapshot,
        AllowanceWindowActivationResult allowanceEvents,
        bool notificationsEnabled,
        CancellationToken cancellationToken)
    {
        var presentation = UsagePresentation.Create(
            snapshot, allowanceEvents, notificationsEnabled, timeProvider.GetLocalNow(), formatProvider);
        var allowance = presentation.Popup.ActivityIndicator;
        var activatedReset = allowance.WindowKind is { } kind ? settings.ReadActivatedReset(kind) : null;
        var sameAccount = !string.IsNullOrWhiteSpace(snapshot.AccountEmail)
            && string.Equals(snapshot.AccountEmail, lastPresentedAccountEmail, StringComparison.OrdinalIgnoreCase);
        var sameWindow = sameAccount && allowance.WindowKind == lastPresentedAllowance.WindowKind;
        var awaitingReset = sameWindow ? awaitingActivationResetAt : null;

        if (activatedReset == allowance.ResetsAt && allowance.ResetsAt is not null)
        {
            awaitingReset = null;
        }
        else if (allowance.AwaitingActivation)
        {
            awaitingReset = allowance.ResetsAt;
        }
        else if (sameWindow
            && lastPresentedAllowance.ResetsAt is { } previousReset
            && allowance.ResetsAt is { } currentReset
            && currentReset != previousReset)
        {
            var resetShift = currentReset - previousReset;
            if (awaitingReset == previousReset
                || (lastPresentedAllowance.RemainingPercent == 100
                    && lastPresentedAllowance.WindowDuration is { } duration
                    && resetShift > TimeSpan.Zero
                    && resetShift < duration / 2))
            {
                awaitingReset = null;
            }
            else if (allowance.RemainingPercent == 100)
            {
                awaitingReset = currentReset;
            }
        }

        allowance = allowance with
        {
            AwaitingActivation = allowance.ResetsAt is not null && awaitingReset == allowance.ResetsAt
        };
        presentation = presentation with { Popup = presentation.Popup with { ActivityIndicator = allowance } };
        cancellationToken.ThrowIfCancellationRequested();
        // Commit visual history only when a complete successful presentation is ready.
        lastPresentedAccountEmail = snapshot.AccountEmail;
        lastPresentedAllowance = allowance;
        awaitingActivationResetAt = awaitingReset;
        return presentation;
    }
}
