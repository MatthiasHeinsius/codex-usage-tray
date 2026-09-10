namespace CodexUsageTray;

internal enum AllowanceWindowKind
{
    FiveHour,
    Weekly
}

[Flags]
internal enum AllowanceWindows
{
    None = 0,
    FiveHour = 1,
    Weekly = 2
}

internal sealed record AllowanceWindowActivationResult(
    AllowanceWindows Reset,
    AllowanceWindows UsedUp,
    AllowanceWindows Confirmed,
    AllowanceWindows Unconfirmed)
{
    public static AllowanceWindowActivationResult Empty { get; } = new(
        AllowanceWindows.None,
        AllowanceWindows.None,
        AllowanceWindows.None,
        AllowanceWindows.None);
}

internal interface IAllowanceWindowActivationCommand
{
    Task SendHiAsync(CancellationToken cancellationToken);
}

internal interface IUsageSnapshotRefresher
{
    Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}

internal interface IAllowanceWindowActivationSettings
{
    bool ActivationEnabled { get; set; }
    bool NotificationsEnabled { get; set; }
    DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window);
    void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset);
}

internal sealed class AllowanceWindowActivation : IDisposable
{
    private readonly IAllowanceWindowActivationCommand command;
    private readonly IUsageSnapshotRefresher snapshots;
    private readonly IAllowanceWindowActivationSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<AllowanceWindowKind, PendingActivation> pending = [];
    private readonly Dictionary<AllowanceWindowKind, AllowanceWindow?> previousWindows = [];
    private readonly SemaphoreSlim observationGate = new(1, 1);
    private bool activationEnabled;
    private bool notificationsEnabled;

    public AllowanceWindowActivation(
        IAllowanceWindowActivationCommand command,
        IUsageSnapshotRefresher snapshots,
        IAllowanceWindowActivationSettings settings,
        TimeProvider timeProvider)
    {
        this.command = command;
        this.snapshots = snapshots;
        this.settings = settings;
        this.timeProvider = timeProvider;
        activationEnabled = settings.ActivationEnabled;
        notificationsEnabled = settings.NotificationsEnabled;
    }

    public void Dispose() => observationGate.Dispose();

    public bool ActivationEnabled
    {
        get => activationEnabled;
        set
        {
            settings.ActivationEnabled = value;
            activationEnabled = value;
        }
    }

    public bool NotificationsEnabled
    {
        get => notificationsEnabled;
        set
        {
            settings.NotificationsEnabled = value;
            notificationsEnabled = value;
        }
    }

    public async Task<AllowanceWindowActivationResult> ObserveAsync(
        UsageSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await observationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            observationGate.Release();
        }
    }

    private async Task<AllowanceWindowActivationResult> ObserveCoreAsync(
        UsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var (reset, usedUp) = DetectTransitions(snapshot);
        var confirmed = ConfirmChangedResets(snapshot);
        if (!activationEnabled)
        {
            return AllowanceWindowActivationResult.Empty with
            {
                Reset = reset,
                UsedUp = usedUp,
                Confirmed = confirmed
            };
        }

        var now = timeProvider.GetUtcNow();
        var targets = new List<AllowanceWindowKind>();
        var unconfirmed = AllowanceWindows.None;
        foreach (var (kind, activation) in pending.ToArray())
        {
            if (Window(snapshot, kind) is { UsedPercent: >= 100 })
            {
                pending.Remove(kind);
                continue;
            }

            if (activation.FailureReported || now < activation.NextAttemptAt)
            {
                continue;
            }

            if (activation.Attempts >= 4)
            {
                activation.FailureReported = true;
                unconfirmed |= Selection(kind);
            }
            else
            {
                targets.Add(kind);
            }
        }

        targets.AddRange(new[] { AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly }
            .Where(kind => !pending.ContainsKey(kind)
                && IsUnusedAndUnconfirmed(kind, Window(snapshot, kind)))
            .ToArray());
        if (targets.Count > 0)
        {
            try
            {
                await command.SendHiAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                UsageSnapshot? fresh = null;
                try
                {
                    fresh = await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    var freshTransitions = DetectTransitions(fresh);
                    reset |= freshTransitions.Reset;
                    usedUp |= freshTransitions.UsedUp;
                    confirmed |= ConfirmChangedResets(fresh);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The normal refresh cycle will try again after the command failure backoff.
                }

                foreach (var target in targets)
                {
                    if ((confirmed & Selection(target)) != 0)
                    {
                        continue;
                    }

                    var window = fresh is null ? null : Window(fresh, target);
                    var originalReset = pending.TryGetValue(target, out var attempted)
                        ? attempted.OriginalReset
                        : Window(snapshot, target)!.ResetsAt!.Value;
                    if (window?.ResetsAt is { } changedReset && changedReset != originalReset)
                    {
                        settings.WriteActivatedReset(target, changedReset);
                        pending.Remove(target);
                        confirmed |= Selection(target);
                        continue;
                    }

                    if (window is { UsedPercent: >= 100 })
                    {
                        pending.Remove(target);
                        continue;
                    }

                    if (pending.TryGetValue(target, out var failedExisting))
                    {
                        failedExisting.NextAttemptAt = now.AddMinutes(5);
                    }
                    else
                    {
                        pending[target] = new PendingActivation(
                            originalReset,
                            attempts: 0,
                            now.AddMinutes(5));
                    }
                }

                return AllowanceWindowActivationResult.Empty with
                {
                    Reset = reset,
                    UsedUp = usedUp,
                    Confirmed = confirmed,
                    Unconfirmed = unconfirmed
                };
            }

            foreach (var target in targets)
            {
                if (pending.TryGetValue(target, out var existing))
                {
                    existing.Attempts++;
                    existing.NextAttemptAt = now.AddMinutes(1);
                }
                else
                {
                    pending[target] = new PendingActivation(
                        Window(snapshot, target)!.ResetsAt!.Value,
                        attempts: 1,
                        now.AddMinutes(1));
                }
            }
        }

        return AllowanceWindowActivationResult.Empty with
        {
            Reset = reset,
            UsedUp = usedUp,
            Confirmed = confirmed,
            Unconfirmed = unconfirmed
        };
    }

    private (AllowanceWindows Reset, AllowanceWindows UsedUp) DetectTransitions(UsageSnapshot snapshot)
    {
        var reset = AllowanceWindows.None;
        var usedUp = AllowanceWindows.None;
        foreach (var kind in new[] { AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly })
        {
            var current = Window(snapshot, kind);
            if (previousWindows.TryGetValue(kind, out var previous))
            {
                if (previous is { UsedPercent: < 100 }
                    && current is { UsedPercent: >= 100 })
                {
                    usedUp |= Selection(kind);
                }

                if (previous is { UsedPercent: >= 100, ResetsAt: { } previousReset }
                    && current is { UsedPercent: 0, ResetsAt: { } currentReset }
                    && currentReset != previousReset)
                {
                    reset |= Selection(kind);
                }
            }

            previousWindows[kind] = current;
        }

        return (reset, usedUp);
    }

    private AllowanceWindows ConfirmChangedResets(UsageSnapshot snapshot)
    {
        var confirmed = AllowanceWindows.None;
        foreach (var (kind, activation) in pending.ToArray())
        {
            if (Window(snapshot, kind)?.ResetsAt is not { } currentReset
                || currentReset == activation.OriginalReset)
            {
                continue;
            }

            settings.WriteActivatedReset(kind, currentReset);
            pending.Remove(kind);
            confirmed |= Selection(kind);
        }

        return confirmed;
    }

    private static AllowanceWindow? Window(UsageSnapshot snapshot, AllowanceWindowKind kind) =>
        kind == AllowanceWindowKind.FiveHour ? snapshot.FiveHour : snapshot.Weekly;

    private bool IsUnusedAndUnconfirmed(AllowanceWindowKind kind, AllowanceWindow? window) =>
        window is { UsedPercent: 0, ResetsAt: { } reset }
        && settings.ReadActivatedReset(kind) != reset;

    private static AllowanceWindows Selection(AllowanceWindowKind kind) =>
        kind == AllowanceWindowKind.FiveHour
            ? AllowanceWindows.FiveHour
            : AllowanceWindows.Weekly;

    private sealed class PendingActivation(
        DateTimeOffset originalReset,
        int attempts,
        DateTimeOffset nextAttemptAt)
    {
        public DateTimeOffset OriginalReset { get; } = originalReset;
        public int Attempts { get; set; } = attempts;
        public DateTimeOffset NextAttemptAt { get; set; } = nextAttemptAt;
        public bool FailureReported { get; set; }
    }
}
