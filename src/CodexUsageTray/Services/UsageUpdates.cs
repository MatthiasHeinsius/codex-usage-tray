namespace CodexUsageTray;

internal sealed record UsageUpdate(
    UsageSnapshot Snapshot,
    UsagePresentation Presentation,
    AllowanceWindowActivationResult AllowanceEvents);

internal sealed class UsageUpdates : IAsyncDisposable
{
    private readonly UsageSnapshots snapshots;
    private readonly AllowanceWindowActivation activation;
    private readonly TimeProvider timeProvider;
    private readonly IFormatProvider formatProvider;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public UsageUpdates(
        UsageSnapshots snapshots,
        AllowanceWindowActivation activation,
        TimeProvider timeProvider,
        IFormatProvider formatProvider)
    {
        this.snapshots = snapshots;
        this.activation = activation;
        this.timeProvider = timeProvider;
        this.formatProvider = formatProvider;
    }

    public Task<UsageUpdate> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(includeActivity: false, cancellationToken);

    public Task<UsageUpdate> RefreshWithActivityAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(includeActivity: true, cancellationToken);

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

    private async Task<UsageUpdate> RefreshAsync(
        bool includeActivity,
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
            var allowanceEvents = await activation.ObserveAsync(snapshot, cancellation.Token).ConfigureAwait(false);
            var finalSnapshot = snapshots.Current ?? snapshot;
            var presentation = UsagePresentation.Create(
                finalSnapshot,
                timeProvider.GetLocalNow(),
                formatProvider);
            return new UsageUpdate(finalSnapshot, presentation, allowanceEvents);
        }
        finally
        {
            updateGate.Release();
        }
    }
}
