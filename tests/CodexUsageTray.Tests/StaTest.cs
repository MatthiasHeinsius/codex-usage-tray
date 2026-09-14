using System.Runtime.ExceptionServices;

namespace CodexUsageTray.Tests;

internal static class StaTest
{
    internal static Task RunAsync(Action action) => RunAsync(_ =>
    {
        action();
        return Task.CompletedTask;
    });

    internal static async Task RunAsync(Func<CancellationToken, Task> action, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await action(cancellation.Token);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        Application.ExitThread();
                    }
                });
                ready.TrySetResult(dispatcher);
                Application.Run();
            }
            catch (Exception exception)
            {
                failure = exception;
                ready.TrySetException(exception);
            }
            finally
            {
                if (failure is null)
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
        })
        {
            // A noncooperative test cannot hold the test process open.
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Exception? testFailure = null;
        try
        {
            await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }

        // Keep pumping while canceled actions unwind their using/finally blocks.
        // Registered callbacks must not bypass the cleanup timeout or replace the original failure.
        try
        {
            await cancellation.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            testFailure ??= exception;
        }

        var threadStopped = thread.Join(TimeSpan.FromSeconds(5));
        if (!threadStopped && ready.Task.IsCompletedSuccessfully)
        {
            try
            {
                var dispatcher = await ready.Task;
                dispatcher.BeginInvoke(Application.ExitThread);
                threadStopped = thread.Join(TimeSpan.FromSeconds(1));
            }
            catch (InvalidOperationException)
            {
                // The dispatcher may already be disposed while the thread exits.
            }
        }

        if (!threadStopped)
        {
            testFailure ??= new TimeoutException("The STA test thread did not stop.");
        }

        if (testFailure is not null)
        {
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }
    }
}
