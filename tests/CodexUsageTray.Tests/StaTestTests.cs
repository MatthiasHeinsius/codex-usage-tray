namespace CodexUsageTray.Tests;

public sealed class StaTestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbacksCannotBlockCleanupOrReplaceTheTimeout(bool blockCallback)
    {
        using var releaseCallback = new ManualResetEventSlim();
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Thread? uiThread = null;
        var run = StaTest.RunAsync(async cancellationToken =>
        {
            uiThread = Thread.CurrentThread;
            registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (blockCallback)
                    {
                        releaseCallback.Wait();
                    }
                    else
                    {
                        throw new IOException("synthetic cancellation failure");
                    }
                }
                finally
                {
                    callbackFinished.TrySetResult();
                }
            });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, timeout: TimeSpan.FromSeconds(2));

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            Assert.True(run.IsCompleted, "The helper must time out without waiting for the blocked callback.");
            Assert.NotNull(uiThread);
            Assert.False(uiThread.IsAlive);
        }
        finally
        {
            releaseCallback.Set();
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            registration.Dispose();
        }
    }

    [Fact]
    public async Task AsyncFailuresDisposeControlsAndStopTheThread()
    {
        var failure = new IOException("synthetic UI failure");
        Thread? uiThread = null;
        var disposed = false;

        var actual = await Assert.ThrowsAsync<IOException>(() => StaTest.RunAsync(async _ =>
        {
            uiThread = Thread.CurrentThread;
            using var control = new Control();
            control.Disposed += (_, _) => disposed = true;
            await Task.Yield();
            Assert.Same(uiThread, Thread.CurrentThread);
            Assert.True(Application.MessageLoop);
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.True(disposed);
        Assert.NotNull(uiThread);
        Assert.False(uiThread.IsAlive);
    }

    [Fact]
    public async Task TimeoutCancelsTheActionAndAllowsAsyncCleanupBeforeStopping()
    {
        Thread? uiThread = null;
        var cleanedUp = false;

        await Assert.ThrowsAsync<TimeoutException>(() => StaTest.RunAsync(async cancellationToken =>
        {
            uiThread = Thread.CurrentThread;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                await Task.Yield();
                Assert.Same(uiThread, Thread.CurrentThread);
                cleanedUp = true;
            }
        }, timeout: TimeSpan.FromSeconds(5)));

        Assert.True(cleanedUp);
        Assert.NotNull(uiThread);
        Assert.False(uiThread.IsAlive);
    }
}
