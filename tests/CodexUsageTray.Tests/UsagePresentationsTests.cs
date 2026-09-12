using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class UsagePresentationsTests
{
    [Fact]
    public async Task ActivityRequestPublishesInitialLoadingAndReadyStates()
    {
        var updates = new ScriptedUsageUpdates();
        var step = updates.Enqueue(UsageUpdateIntent.Activity);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);

        var request = presentations.RequestAsync(UsageUpdateIntent.Activity);

        Assert.Collection(
            sink.Presentations,
            presentation => Assert.IsType<UsagePresentation.Initial>(presentation),
            presentation => Assert.IsType<UsagePresentation.Loading>(presentation));
        Assert.Equal([UsageUpdateIntent.Activity], updates.Requests);

        step.Succeed(CreateReady(usedPercent: 25));
        await request;

        var ready = Assert.IsType<UsagePresentation.Ready>(sink.Presentations[^1]);
        Assert.Equal("75% left", ready.Popup.FiveHour.RemainingText);
        Assert.Equal(75, ready.Tray.FiveHourRemaining);
    }

    [Fact]
    public async Task UpdateFailurePublishesOneLineFailedStateAndCompletesNormally()
    {
        var updates = new ScriptedUsageUpdates();
        var step = updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);

        var request = presentations.RequestAsync(UsageUpdateIntent.Routine);
        step.Fail(new IOException("Account read failed.\r\nTry again."));
        await request;

        var failed = Assert.IsType<UsagePresentation.Failed>(sink.Presentations[^1]);
        Assert.Equal("Account read failed. Try again.", failed.FailureMessage);
        Assert.Equal("Could not refresh", failed.Popup.AccountStatus);
        Assert.Equal("Unavailable", failed.Popup.FiveHour.RemainingText);
        Assert.Contains("unavailable", failed.Tray.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedUpdateRetainsAndMarksTheLastSuccessfulValuesStale()
    {
        var updates = new ScriptedUsageUpdates();
        var success = updates.Enqueue(UsageUpdateIntent.Routine);
        var failure = updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);

        var successfulRequest = presentations.RequestAsync(UsageUpdateIntent.Routine);
        success.Succeed(CreateReady(usedPercent: 20));
        await successfulRequest;
        var failedRequest = presentations.RequestAsync(UsageUpdateIntent.Routine);
        failure.Fail(new IOException("Synthetic failure."));
        await failedRequest;

        var failed = Assert.IsType<UsagePresentation.Failed>(sink.Presentations[^1]);
        Assert.Equal("80% left", failed.Popup.FiveHour.RemainingText);
        Assert.StartsWith("Stale · Synthetic failure.", failed.Popup.UpdatedText, StringComparison.Ordinal);
        Assert.Equal(80, failed.Tray.FiveHourRemaining);
        Assert.Contains("stale", failed.Tray.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRequestsShareLoadingAndPublishEveryOutcome()
    {
        var updates = new ScriptedUsageUpdates();
        var firstStep = updates.Enqueue(UsageUpdateIntent.Routine);
        var secondStep = updates.Enqueue(UsageUpdateIntent.Activity);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);

        var firstRequest = presentations.RequestAsync(UsageUpdateIntent.Routine);
        var secondRequest = presentations.RequestAsync(UsageUpdateIntent.Activity);

        Assert.Equal(2, sink.Presentations.Count);
        Assert.IsType<UsagePresentation.Loading>(sink.Presentations[^1]);

        firstStep.Succeed(CreateReady(usedPercent: 20, withNotice: true));
        await firstRequest;

        var intermediate = Assert.IsType<UsagePresentation.Loading>(sink.Presentations[^1]);
        Assert.Equal("80% left", intermediate.Popup.FiveHour.RemainingText);
        Assert.Single(intermediate.Notices);

        secondStep.Succeed(CreateReady(usedPercent: 30));
        await secondRequest;

        var ready = Assert.IsType<UsagePresentation.Ready>(sink.Presentations[^1]);
        Assert.Equal("70% left", ready.Popup.FiveHour.RemainingText);
        Assert.Empty(ready.Notices);
        Assert.Equal(4, sink.Presentations.Count);
    }

    [Fact]
    public async Task IntermediateFailureStaysLoadingUntilTheQueuedRequestSucceeds()
    {
        var updates = new ScriptedUsageUpdates();
        var firstStep = updates.Enqueue(UsageUpdateIntent.Routine);
        var secondStep = updates.Enqueue(UsageUpdateIntent.Activity);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);

        var firstRequest = presentations.RequestAsync(UsageUpdateIntent.Routine);
        var secondRequest = presentations.RequestAsync(UsageUpdateIntent.Activity);
        firstStep.Fail(new IOException("Routine update failed."));
        await firstRequest;

        var intermediate = Assert.IsType<UsagePresentation.Loading>(sink.Presentations[^1]);
        Assert.Equal("Routine update failed.", intermediate.FailureMessage);
        Assert.Equal("Reading your Codex account", intermediate.Popup.AccountStatus);

        secondStep.Succeed(CreateReady(usedPercent: 30));
        await secondRequest;

        var ready = Assert.IsType<UsagePresentation.Ready>(sink.Presentations[^1]);
        Assert.Equal("70% left", ready.Popup.FiveHour.RemainingText);
    }

    [Fact]
    public async Task CallerCancellationRestoresThePreviousSettledStateWithoutFailure()
    {
        var updates = new ScriptedUsageUpdates();
        updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);
        using var cancellation = new CancellationTokenSource();

        var request = presentations.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Collection(
            sink.Presentations,
            presentation => Assert.IsType<UsagePresentation.Initial>(presentation),
            presentation => Assert.IsType<UsagePresentation.Loading>(presentation),
            presentation => Assert.IsType<UsagePresentation.Initial>(presentation));
    }

    [Fact]
    public async Task AlreadyCanceledRequestPublishesNothing()
    {
        var updates = new ScriptedUsageUpdates();
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => presentations.RequestAsync(UsageUpdateIntent.Routine, cancellation.Token));

        Assert.Single(sink.Presentations);
        Assert.Empty(updates.Requests);
    }

    [Fact]
    public async Task UnexpectedUpdateCancellationPublishesFailedState()
    {
        var updates = new ScriptedUsageUpdates();
        var step = updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink();
        await using var presentations = new UsagePresentations(updates, sink);

        var request = presentations.RequestAsync(UsageUpdateIntent.Routine);
        step.Fail(new OperationCanceledException("Internal cancellation."));
        await request;

        var failed = Assert.IsType<UsagePresentation.Failed>(sink.Presentations[^1]);
        Assert.Equal("Internal cancellation.", failed.FailureMessage);
    }

    [Fact]
    public async Task PreferencesAndLifetimeStayBehindThePresentationModule()
    {
        var updates = new ScriptedUsageUpdates
        {
            ActivationEnabled = false,
            NotificationsEnabled = true
        };
        var presentations = new UsagePresentations(updates, new RecordingSink());

        Assert.False(presentations.ActivationEnabled);
        Assert.True(presentations.NotificationsEnabled);

        presentations.ActivationEnabled = true;
        presentations.NotificationsEnabled = false;
        await presentations.DisposeAsync();

        Assert.True(updates.ActivationEnabled);
        Assert.False(updates.NotificationsEnabled);
        Assert.True(updates.Disposed);
        Assert.Throws<ObjectDisposedException>(() => _ = presentations.ActivationEnabled);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => presentations.RequestAsync(UsageUpdateIntent.Routine));
    }

    [Fact]
    public async Task PresentationAdapterFailurePropagates()
    {
        var updates = new ScriptedUsageUpdates();
        var step = updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink { ThrowOnReady = true };
        await using var presentations = new UsagePresentations(updates, sink);

        var request = presentations.RequestAsync(UsageUpdateIntent.Routine);
        step.Succeed(CreateReady(usedPercent: 20));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Equal("Synthetic presentation failure.", failure.Message);
        Assert.DoesNotContain(sink.Presentations, presentation => presentation is UsagePresentation.Failed);
    }

    [Fact]
    public async Task LoadingAdapterFailureDoesNotCorruptTheNextRequest()
    {
        var updates = new ScriptedUsageUpdates();
        var sink = new RecordingSink { ThrowOnNextLoading = true };
        await using var presentations = new UsagePresentations(updates, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => presentations.RequestAsync(UsageUpdateIntent.Routine));
        Assert.Empty(updates.Requests);

        var step = updates.Enqueue(UsageUpdateIntent.Routine);
        var retry = presentations.RequestAsync(UsageUpdateIntent.Routine);
        step.Succeed(CreateReady(usedPercent: 20));
        await retry;

        Assert.IsType<UsagePresentation.Ready>(sink.Presentations[^1]);
    }

    [Fact]
    public async Task DisposalCancelsRequestsWithoutPublishingAnotherState()
    {
        var updates = new ScriptedUsageUpdates();
        updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink();
        var presentations = new UsagePresentations(updates, sink);
        var request = presentations.RequestAsync(UsageUpdateIntent.Routine);

        await presentations.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(2, sink.Presentations.Count);
        Assert.IsType<UsagePresentation.Loading>(sink.Presentations[^1]);
    }

    [Fact]
    public async Task SuccessfulUpdateFinishingAfterDisposalIsNotPublished()
    {
        var updates = new ScriptedUsageUpdates { CancelActiveOnDispose = false };
        var step = updates.Enqueue(UsageUpdateIntent.Routine);
        var sink = new RecordingSink();
        var presentations = new UsagePresentations(updates, sink);
        var request = presentations.RequestAsync(UsageUpdateIntent.Routine);

        await presentations.DisposeAsync();
        step.Succeed(CreateReady(usedPercent: 20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(2, sink.Presentations.Count);
        Assert.IsType<UsagePresentation.Loading>(sink.Presentations[^1]);
    }

    [Fact]
    public async Task SinkDispatchDoesNotHoldTheStateLock()
    {
        var updates = new ScriptedUsageUpdates();
        var firstStep = updates.Enqueue(UsageUpdateIntent.Routine);
        var secondStep = updates.Enqueue(UsageUpdateIntent.Activity);
        var sink = new BlockingReadySink();
        await using var presentations = new UsagePresentations(updates, sink);

        var firstRequest = Task.Run(
            () => presentations.RequestAsync(UsageUpdateIntent.Routine));
        firstStep.Succeed(CreateReady(usedPercent: 20));
        await sink.ReadyDeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task? secondRequest = null;
        var secondCallReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invokeSecondRequest = Task.Run(() =>
        {
            secondRequest = presentations.RequestAsync(UsageUpdateIntent.Activity);
            secondCallReturned.SetResult();
        });

        try
        {
            await secondCallReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            sink.ReleaseReadyDelivery.Set();
        }

        await invokeSecondRequest;
        await firstRequest;
        secondStep.Succeed(CreateReady(usedPercent: 30));
        await secondRequest!;

        Assert.Equal(
            [UsageUpdateIntent.Routine, UsageUpdateIntent.Activity],
            updates.Requests);
    }

    [Fact]
    public void PublishedNoticesCannotBeMutated()
    {
        var presentation = CreateReady(usedPercent: 20, withNotice: true);
        var notices = (ICollection<UsagePresentation.NoticePresentation>)presentation.Notices;

        Assert.True(notices.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => notices.Clear());
    }

    private static UsagePresentation.Ready CreateReady(int usedPercent, bool withNotice = false)
    {
        var now = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
        var snapshot = new UsageSnapshot(
            now,
            now,
            new AllowanceWindow(usedPercent, TimeSpan.FromHours(5), now.AddHours(5)),
            weekly: null,
            lifetimeTokens: null,
            todayTokens: null,
            plan: "plus",
            limitName: "Codex");
        var events = withNotice
            ? AllowanceWindowActivationResult.Empty with { UsedUp = AllowanceWindows.FiveHour }
            : AllowanceWindowActivationResult.Empty;
        return UsagePresentation.Create(
            snapshot,
            events,
            notificationsEnabled: withNotice,
            now,
            CultureInfo.InvariantCulture);
    }

    private sealed class RecordingSink : IUsagePresentationSink
    {
        public List<UsagePresentation> Presentations { get; } = [];
        public bool ThrowOnReady { get; init; }
        public bool ThrowOnNextLoading { get; set; }

        public void Present(UsagePresentation presentation)
        {
            if (ThrowOnNextLoading && presentation is UsagePresentation.Loading)
            {
                ThrowOnNextLoading = false;
                throw new InvalidOperationException("Synthetic loading presentation failure.");
            }

            if (ThrowOnReady && presentation is UsagePresentation.Ready)
            {
                throw new InvalidOperationException("Synthetic presentation failure.");
            }

            Presentations.Add(presentation);
        }
    }

    private sealed class ScriptedUsageUpdates : IUsageUpdates
    {
        private readonly Queue<UpdateStep> steps = [];
        private readonly List<UpdateStep> active = [];
        private readonly List<UsageUpdateIntent> requests = [];

        public bool ActivationEnabled { get; set; }
        public bool NotificationsEnabled { get; set; }
        public bool Disposed { get; private set; }
        public bool CancelActiveOnDispose { get; init; } = true;
        public UsageUpdateIntent[] Requests => requests.ToArray();

        public UpdateStep Enqueue(UsageUpdateIntent intent)
        {
            var step = new UpdateStep(intent);
            steps.Enqueue(step);
            return step;
        }

        public Task<UsagePresentation.Ready> RefreshAsync(CancellationToken cancellationToken = default) =>
            RunAsync(UsageUpdateIntent.Routine, cancellationToken);

        public Task<UsagePresentation.Ready> RefreshWithActivityAsync(
            CancellationToken cancellationToken = default) =>
            RunAsync(UsageUpdateIntent.Activity, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (CancelActiveOnDispose)
            {
                foreach (var step in steps.Concat(active))
                {
                    step.Cancel();
                }
            }

            return ValueTask.CompletedTask;
        }

        private Task<UsagePresentation.Ready> RunAsync(
            UsageUpdateIntent intent,
            CancellationToken cancellationToken)
        {
            requests.Add(intent);
            var step = steps.Dequeue();
            Assert.Equal(step.Intent, intent);
            active.Add(step);
            return step.Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class UpdateStep(UsageUpdateIntent intent)
    {
        public UsageUpdateIntent Intent { get; } = intent;
        public TaskCompletionSource<UsagePresentation.Ready> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Succeed(UsagePresentation.Ready presentation) => Completion.TrySetResult(presentation);
        public void Fail(Exception exception) => Completion.TrySetException(exception);
        public void Cancel() => Completion.TrySetCanceled();
    }

    private sealed class BlockingReadySink : IUsagePresentationSink
    {
        private int readyDeliveries;

        public TaskCompletionSource ReadyDeliveryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseReadyDelivery { get; } = new(initialState: false);

        public void Present(UsagePresentation presentation)
        {
            if (presentation is UsagePresentation.Ready
                && Interlocked.Increment(ref readyDeliveries) == 1)
            {
                ReadyDeliveryStarted.SetResult();
                ReleaseReadyDelivery.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }
}
