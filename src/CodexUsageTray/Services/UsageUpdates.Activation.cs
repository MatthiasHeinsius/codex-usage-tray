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
        var unconfirmed = AllowanceWindows.None;
        foreach (var (kind, activation) in pendingActivations.ToArray())
        {
            if (Window(snapshot, kind) is { UsedPercent: >= 100 })
            {
                pendingActivations.Remove(kind);
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
                    var originalReset = pendingActivations.TryGetValue(target, out var attempted)
                        ? attempted.OriginalReset
                        : Window(snapshot, target)!.ResetsAt!.Value;
                    if (window?.ResetsAt is { } changedReset && changedReset != originalReset)
                    {
                        settings.WriteActivatedReset(target, changedReset);
                        pendingActivations.Remove(target);
                        confirmed |= Selection(target);
                        continue;
                    }

                    if (window is { UsedPercent: >= 100 })
                    {
                        pendingActivations.Remove(target);
                        continue;
                    }

                    if (pendingActivations.TryGetValue(target, out var failedExisting))
                    {
                        failedExisting.NextAttemptAt = now.AddMinutes(5);
                    }
                    else
                    {
                        pendingActivations[target] = new PendingActivation(
                            originalReset,
                            attempts: 0,
                            now.AddMinutes(5));
                    }
                }

                return (
                    finalSnapshot,
                    AllowanceWindowActivationResult.Empty with
                    {
                        Reset = reset,
                        UsedUp = usedUp,
                        Confirmed = confirmed,
                        Unconfirmed = unconfirmed
                    });
            }

            foreach (var target in targets)
            {
                if (pendingActivations.TryGetValue(target, out var existing))
                {
                    existing.Attempts++;
                    existing.NextAttemptAt = now.AddMinutes(1);
                }
                else
                {
                    pendingActivations[target] = new PendingActivation(
                        Window(snapshot, target)!.ResetsAt!.Value,
                        attempts: 1,
                        now.AddMinutes(1));
                }
            }
        }

        return (
            finalSnapshot,
            AllowanceWindowActivationResult.Empty with
            {
                Reset = reset,
                UsedUp = usedUp,
                Confirmed = confirmed,
                Unconfirmed = unconfirmed
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
        foreach (var (kind, activation) in pendingActivations.ToArray())
        {
            if (Window(snapshot, kind)?.ResetsAt is not { } currentReset
                || currentReset == activation.OriginalReset)
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
