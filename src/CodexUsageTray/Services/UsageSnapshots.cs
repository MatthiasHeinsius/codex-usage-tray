namespace CodexUsageTray;

internal enum UsageObservationRequest
{
    AllowanceWindows,
    AllowanceWindowsAndActivity
}

internal interface IUsageObservationReader
{
    Task<UsageObservations> ReadAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class UsageSnapshots : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly IUsageObservationReader observations;
    private readonly CancellationTokenSource lifetime = new();
    private UsageSnapshot? current;
    private RefreshWave? activeWave;
    private bool disposed;

    internal UsageSnapshots(IUsageObservationReader observations)
    {
        this.observations = observations;
    }

    public UsageSnapshot? Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(UsageObservationRequest.AllowanceWindows, cancellationToken);

    public Task<UsageSnapshot> RefreshWithActivityAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(UsageObservationRequest.AllowanceWindowsAndActivity, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Task? running;
        RefreshWave? wave;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            wave = activeWave;
            running = wave?.Runner;
            wave?.Completion.TrySetCanceled(new CancellationToken(canceled: true));
        }

        lifetime.Cancel();
        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposal owns cancellation of the shared adapter read.
            }
        }

        lifetime.Dispose();
    }

    private Task<UsageSnapshot> RequestAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<UsageSnapshot> shared;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (activeWave is null)
            {
                activeWave = new RefreshWave(request, current);
                var wave = activeWave;
                wave.Runner = Task.Run(() => RunWaveAsync(wave), CancellationToken.None);
            }
            else if (request == UsageObservationRequest.AllowanceWindowsAndActivity)
            {
                activeWave.ActivityRequested = true;
            }

            shared = activeWave.Completion.Task;
        }

        return cancellationToken.CanBeCanceled
            ? shared.WaitAsync(cancellationToken)
            : shared;
    }

    private async Task RunWaveAsync(RefreshWave wave)
    {
        var request = wave.InitialRequest;
        var candidate = wave.Previous;

        while (true)
        {
            try
            {
                var observed = await observations.ReadAsync(request, lifetime.Token).ConfigureAwait(false);
                candidate = UsageSnapshot.Reconcile(candidate, observed.Account, observed.Local);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                CompleteCanceled(wave);
                return;
            }
            catch (Exception exception)
            {
                if (ContinueWithActivityOrCompleteFailed(wave, request, exception))
                {
                    request = UsageObservationRequest.AllowanceWindowsAndActivity;
                    continue;
                }

                return;
            }

            lock (sync)
            {
                if (disposed)
                {
                    activeWave = null;
                    wave.Completion.TrySetCanceled(new CancellationToken(canceled: true));
                    return;
                }

                if (request == UsageObservationRequest.AllowanceWindows && wave.ActivityRequested)
                {
                    request = UsageObservationRequest.AllowanceWindowsAndActivity;
                    continue;
                }

                current = candidate;
                activeWave = null;
                wave.Completion.TrySetResult(candidate);
                return;
            }
        }
    }

    private bool ContinueWithActivityOrCompleteFailed(
        RefreshWave wave,
        UsageObservationRequest request,
        Exception exception)
    {
        var failure = exception as UsageSnapshotRefreshException
            ?? new UsageSnapshotRefreshException(exception.Message, exception);

        lock (sync)
        {
            if (!disposed
                && request == UsageObservationRequest.AllowanceWindows
                && wave.ActivityRequested)
            {
                return true;
            }

            if (ReferenceEquals(activeWave, wave))
            {
                activeWave = null;
            }

            if (disposed)
            {
                wave.Completion.TrySetCanceled(new CancellationToken(canceled: true));
            }
            else
            {
                wave.Completion.TrySetException(failure);
            }

            return false;
        }
    }

    private void CompleteCanceled(RefreshWave wave)
    {
        lock (sync)
        {
            if (ReferenceEquals(activeWave, wave))
            {
                activeWave = null;
            }

            wave.Completion.TrySetCanceled(lifetime.Token);
        }
    }

    private sealed class RefreshWave(
        UsageObservationRequest initialRequest,
        UsageSnapshot? previous)
    {
        public UsageObservationRequest InitialRequest { get; } = initialRequest;
        public UsageSnapshot? Previous { get; } = previous;
        public bool ActivityRequested { get; set; } =
            initialRequest == UsageObservationRequest.AllowanceWindowsAndActivity;
        public TaskCompletionSource<UsageSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Runner { get; set; }
    }
}

internal sealed class UsageSnapshotRefreshException : Exception
{
    public UsageSnapshotRefreshException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
