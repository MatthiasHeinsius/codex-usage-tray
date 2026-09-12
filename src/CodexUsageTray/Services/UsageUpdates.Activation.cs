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
    AllowanceWindows Confirmed)
{
    public static AllowanceWindowActivationResult Empty { get; } = new(
        AllowanceWindows.None,
        AllowanceWindows.None,
        AllowanceWindows.None);
}

internal interface IAllowanceWindowActivationCommand
{
    Task SendHiAsync(CancellationToken cancellationToken);
}

internal interface IAllowanceWindowActivationSettings
{
    bool ActivationEnabled { get; set; }
    bool NotificationsEnabled { get; set; }
    DateTimeOffset? ReadActivatedReset(AllowanceWindowKind window);
    void WriteActivatedReset(AllowanceWindowKind window, DateTimeOffset reset);
}

internal sealed partial class UsageUpdates
{
    private readonly Dictionary<AllowanceWindowKind, PendingActivation> pendingActivations = [];
    private readonly Dictionary<AllowanceWindowKind, AllowanceWindow?> previousWindows = [];

    private async Task<(UsageSnapshot Snapshot, AllowanceWindowActivationResult Events)>
        ObserveAllowanceWindowsAsync(
            bool activationEnabled,
            UsageSnapshot snapshot,
            CancellationToken cancellationToken)
    {
        var finalSnapshot = snapshot;
        var (reset, usedUp) = DetectAllowanceWindowTransitions(snapshot);
        var confirmed = ConfirmChangedResets(snapshot);
        if (!activationEnabled)
        {
            return (
                finalSnapshot,
                AllowanceWindowActivationResult.Empty with
                {
                    Reset = reset,
                    UsedUp = usedUp,
                    Confirmed = confirmed
                });
        }

        var now = timeProvider.GetUtcNow();
        var targets = new List<AllowanceWindowKind>();
        foreach (var (kind, activation) in pendingActivations.ToArray())
        {
            switch (activation.TakeNextAction(Window(snapshot, kind), now))
            {
                case PendingActivationAction.Wait:
                    break;
                case PendingActivationAction.Cancel:
                    pendingActivations.Remove(kind);
                    break;
                case PendingActivationAction.Request:
                    targets.Add(kind);
                    break;
            }
        }

        targets.AddRange(new[] { AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly }
            .Where(kind => !pendingActivations.ContainsKey(kind)
                && IsUnusedAndUnconfirmed(kind, Window(snapshot, kind)))
            .ToArray());
        if (targets.Count > 0)
        {
            try
            {
                await activationCommand.SendHiAsync(cancellationToken).ConfigureAwait(false);
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
                    fresh = await RefreshSnapshotAsync(includeActivity: false, cancellationToken)
                        .ConfigureAwait(false);
                    finalSnapshot = fresh;
                    var freshTransitions = DetectAllowanceWindowTransitions(fresh);
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
                    // The normal Usage Update will try again after the command failure backoff.
                }

                foreach (var target in targets)
                {
                    if ((confirmed & Selection(target)) != 0)
                    {
                        continue;
                    }

                    var window = fresh is null ? null : Window(fresh, target);
                    var activation = pendingActivations.GetValueOrDefault(target)
                        ?? new PendingActivation(Window(snapshot, target)!.ResetsAt!.Value);
                    if (activation.ConfirmedReset(window) is { } changedReset)
                    {
                        settings.WriteActivatedReset(target, changedReset);
                        pendingActivations.Remove(target);
                        confirmed |= Selection(target);
                        continue;
                    }

                    if (window?.IsUsedUp == true)
                    {
                        pendingActivations.Remove(target);
                        continue;
                    }

                    activation.RecordRequestFailed(now);
                    pendingActivations[target] = activation;
                }

                return (
                    finalSnapshot,
                    AllowanceWindowActivationResult.Empty with
                    {
                        Reset = reset,
                        UsedUp = usedUp,
                        Confirmed = confirmed
                    });
            }

            foreach (var target in targets)
            {
                var activation = pendingActivations.GetValueOrDefault(target)
                    ?? new PendingActivation(Window(snapshot, target)!.ResetsAt!.Value);
                activation.RecordCompletedAttempt(now);
                pendingActivations[target] = activation;
            }
        }

        return (
            finalSnapshot,
            AllowanceWindowActivationResult.Empty with
            {
                Reset = reset,
                UsedUp = usedUp,
                Confirmed = confirmed
            });
    }

    private (AllowanceWindows Reset, AllowanceWindows UsedUp)
        DetectAllowanceWindowTransitions(UsageSnapshot snapshot)
    {
        var reset = AllowanceWindows.None;
        var usedUp = AllowanceWindows.None;
        foreach (var kind in new[] { AllowanceWindowKind.FiveHour, AllowanceWindowKind.Weekly })
        {
            var current = Window(snapshot, kind);
            if (previousWindows.TryGetValue(kind, out var previous))
            {
                if (previous is { IsUsedUp: false } && current?.IsUsedUp == true)
                {
                    usedUp |= Selection(kind);
                }

                if (previous is { } prior && current?.IsResetOf(prior) == true)
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
        foreach (var (kind, activation) in pendingActivations.ToArray())
        {
            if (activation.ConfirmedReset(Window(snapshot, kind)) is not { } currentReset)
            {
                continue;
            }

            settings.WriteActivatedReset(kind, currentReset);
            pendingActivations.Remove(kind);
            confirmed |= Selection(kind);
        }

        return confirmed;
    }

    private static AllowanceWindow? Window(UsageSnapshot snapshot, AllowanceWindowKind kind) =>
        kind == AllowanceWindowKind.FiveHour ? snapshot.FiveHour : snapshot.Weekly;

    private bool IsUnusedAndUnconfirmed(AllowanceWindowKind kind, AllowanceWindow? window) =>
        window is { IsUnused: true, ResetsAt: { } reset }
        && settings.ReadActivatedReset(kind) != reset;

    private static AllowanceWindows Selection(AllowanceWindowKind kind) =>
        kind == AllowanceWindowKind.FiveHour
            ? AllowanceWindows.FiveHour
            : AllowanceWindows.Weekly;

    private enum PendingActivationAction
    {
        Wait,
        Request,
        Cancel
    }

    private sealed class PendingActivation(DateTimeOffset originalReset)
    {
        private int attempts;
        private DateTimeOffset nextAttemptAt;

        public PendingActivationAction TakeNextAction(AllowanceWindow? window, DateTimeOffset now)
        {
            if (window?.IsUsedUp == true)
            {
                return PendingActivationAction.Cancel;
            }

            if (now < nextAttemptAt || attempts >= 4)
            {
                return PendingActivationAction.Wait;
            }

            return PendingActivationAction.Request;
        }

        public DateTimeOffset? ConfirmedReset(AllowanceWindow? window) =>
            window?.ResetsAt is { } reset && reset != originalReset ? reset : null;

        public void RecordCompletedAttempt(DateTimeOffset now)
        {
            attempts++;
            nextAttemptAt = now.AddMinutes(1);
        }

        public void RecordRequestFailed(DateTimeOffset now) =>
            nextAttemptAt = now.AddMinutes(5);
    }
}
