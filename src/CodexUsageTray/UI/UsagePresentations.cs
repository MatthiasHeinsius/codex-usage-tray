namespace CodexUsageTray;

internal enum UsageUpdateIntent
{
    Routine,
    Activity
}

internal interface IUsagePresentationSink
{
    void Present(UsagePresentation presentation);
}

internal sealed class UsagePresentations : IAsyncDisposable
{
    private readonly IUsageUpdates updates;
    private readonly IUsagePresentationSink sink;
    private readonly object stateSync = new();
    private readonly Queue<PendingPresentation> presentationQueue = [];
    private UsagePresentation.Ready? lastSuccessful;
    private string? lastFailureMessage;
    private int pendingRequests;
    private bool deliveringPresentations;
    private int disposed;

    internal UsagePresentations(IUsageUpdates updates, IUsagePresentationSink sink)
    {
        this.updates = updates;
        this.sink = sink;
        sink.Present(UsagePresentation.CreateInitial());
    }

    public static UsagePresentations CreateDefault(IUsagePresentationSink sink) =>
        new(UsageUpdates.CreateDefault(), sink);

    public bool ActivationEnabled
    {
        get
        {
            ThrowIfDisposed();
            return updates.ActivationEnabled;
        }
        set
        {
            ThrowIfDisposed();
            updates.ActivationEnabled = value;
        }
    }

    public bool NotificationsEnabled
    {
        get
        {
            ThrowIfDisposed();
            return updates.NotificationsEnabled;
        }
        set
        {
            ThrowIfDisposed();
            updates.NotificationsEnabled = value;
        }
    }

    public async Task RequestAsync(
        UsageUpdateIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (intent is not UsageUpdateIntent.Routine and not UsageUpdateIntent.Activity)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown Usage Update intent.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await BeginRequestAsync();

        UsagePresentation.Ready presentation;
        try
        {
            presentation = intent switch
            {
                UsageUpdateIntent.Routine => await updates.RefreshAsync(cancellationToken),
                UsageUpdateIntent.Activity => await updates.RefreshWithActivityAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown Usage Update intent.")
            };
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
        {
            await CompleteCanceledRequestAsync();
            throw;
        }
        catch (Exception exception)
        {
            if (await CompleteFailedRequestAsync(OneLine(exception.Message)))
            {
                throw new OperationCanceledException("The Usage Presentations module was disposed.");
            }

            return;
        }

        if (await CompleteSuccessfullyAsync(presentation))
        {
            throw new OperationCanceledException("The Usage Presentations module was disposed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await updates.DisposeAsync().ConfigureAwait(false);
    }

    private async Task BeginRequestAsync()
    {
        var completion = Task.CompletedTask;
        var shouldDeliver = false;
        ThrowIfDisposed();
        lock (stateSync)
        {
            ThrowIfDisposed();
            pendingRequests++;
            if (pendingRequests == 1)
            {
                completion = EnqueuePresentationLocked(
                    UsagePresentation.CreateLoading(lastSuccessful, lastFailureMessage),
                    out shouldDeliver);
            }
        }

        DeliverPresentationsIfNeeded(shouldDeliver);
        try
        {
            await completion;
        }
        catch
        {
            AbandonRequest();
            throw;
        }
    }

    private async Task<bool> CompleteSuccessfullyAsync(UsagePresentation.Ready presentation)
    {
        Task completion;
        bool shouldDeliver;
        lock (stateSync)
        {
            pendingRequests--;
            if (Volatile.Read(ref disposed) != 0)
            {
                return true;
            }

            lastSuccessful = presentation.WithoutNotices();
            lastFailureMessage = null;
            completion = EnqueuePresentationLocked(
                pendingRequests == 0
                    ? presentation
                    : UsagePresentation.CreateLoading(lastSuccessful, notices: presentation.Notices),
                out shouldDeliver);
        }

        DeliverPresentationsIfNeeded(shouldDeliver);
        await completion;
        return false;
    }

    private async Task<bool> CompleteFailedRequestAsync(string failureMessage)
    {
        Task completion;
        bool shouldDeliver;
        lock (stateSync)
        {
            pendingRequests--;
            if (Volatile.Read(ref disposed) != 0)
            {
                return true;
            }

            lastFailureMessage = failureMessage;
            completion = EnqueuePresentationLocked(
                pendingRequests == 0
                    ? UsagePresentation.CreateFailed(lastSuccessful, failureMessage)
                    : UsagePresentation.CreateLoading(lastSuccessful, failureMessage),
                out shouldDeliver);
        }

        DeliverPresentationsIfNeeded(shouldDeliver);
        await completion;
        return false;
    }

    private async Task CompleteCanceledRequestAsync()
    {
        var completion = Task.CompletedTask;
        var shouldDeliver = false;
        lock (stateSync)
        {
            pendingRequests--;
            if (Volatile.Read(ref disposed) != 0 || pendingRequests != 0)
            {
                return;
            }

            completion = EnqueuePresentationLocked(
                lastFailureMessage is { } failureMessage
                    ? UsagePresentation.CreateFailed(lastSuccessful, failureMessage)
                    : lastSuccessful is { } successful
                        ? successful
                        : UsagePresentation.CreateInitial(),
                out shouldDeliver);
        }

        DeliverPresentationsIfNeeded(shouldDeliver);
        await completion;
    }

    private void AbandonRequest()
    {
        lock (stateSync)
        {
            pendingRequests--;
        }
    }

    private Task EnqueuePresentationLocked(
        UsagePresentation presentation,
        out bool shouldDeliver)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presentationQueue.Enqueue(new PendingPresentation(presentation, completion));
        shouldDeliver = !deliveringPresentations;
        deliveringPresentations = true;
        return completion.Task;
    }

    private void DeliverPresentationsIfNeeded(bool shouldDeliver)
    {
        if (!shouldDeliver)
        {
            return;
        }

        while (true)
        {
            PendingPresentation delivery;
            lock (stateSync)
            {
                if (presentationQueue.Count == 0)
                {
                    deliveringPresentations = false;
                    return;
                }

                delivery = presentationQueue.Dequeue();
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                delivery.Completion.TrySetCanceled();
                continue;
            }

            try
            {
                sink.Present(delivery.Presentation);
                delivery.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                delivery.Completion.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static string OneLine(string value)
    {
        var lines = value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "The Usage Update failed." : string.Join(" ", lines);
    }

    private sealed record PendingPresentation(
        UsagePresentation Presentation,
        TaskCompletionSource Completion);
}
