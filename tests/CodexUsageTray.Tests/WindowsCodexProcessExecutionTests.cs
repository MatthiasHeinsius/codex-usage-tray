using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace CodexUsageTray.Tests;

[Collection<ProcessEnvironmentIsolation>]
public sealed class WindowsCodexProcessExecutionTests(ITestOutputHelper output)
{
    private static string ProcessFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "CodexUsageTray.ProcessFixture.exe");

    [Fact]
    public async Task CaptureReturnsArgumentsStreamsAndExitCode()
    {
        await WithCommandAsync(
            """
            @echo off
            echo stdout:%*
            1>&2 echo stderr:%*
            exit /b 7
            """,
            async execution =>
            {
                var output = await execution.CaptureAsync(
                    "alpha \"two words\"",
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None);

                Assert.Equal(7, output.ExitCode);
                Assert.Contains("stdout:alpha \"two words\"", output.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("stderr:alpha \"two words\"", output.StandardError, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task ExchangeLinesWritesFlushesAndReadsWithStandardErrorDiagnostics()
    {
        await WithCommandAsync(
            """
            @echo off
            set /p request=
            echo reply:%request%
            1>&2 echo earlier diagnostic
            1>&2 echo diagnostic
            1>&2 echo.
            """,
            async execution =>
            {
                ICodexLineExchange? usedLines = null;
                var response = await execution.ExchangeLinesAsync(
                    "app-server --stdio",
                    TimeSpan.FromSeconds(5),
                    async lines =>
                    {
                        usedLines = lines;
                        await lines.WriteLineAsync("ping");
                        return await lines.ReadLineAsync();
                    },
                    CancellationToken.None);

                Assert.Equal("reply:ping", response);
                Assert.Equal("diagnostic", usedLines?.LastStandardErrorLine);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureCancellationStopsTheCommandProcess(bool timeout)
    {
        await WithCommandAsync(
            $"""
            @echo off
            > "%~1.started" echo started
            "{ProcessFixturePath}" delay
            > "%~1.finished" echo finished
            """,
            async (execution, directory) =>
            {
                var markerPath = directory.FilePath("capture");
                using var cancellation = new CancellationTokenSource();
                var capture = execution.CaptureAsync(
                    $"\"{markerPath}\"",
                    timeout ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5),
                    cancellation.Token);
                await WaitForFileAsync(markerPath + ".started");

                if (!timeout)
                {
                    cancellation.Cancel();
                }

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => capture.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                Assert.False(File.Exists(markerPath + ".finished"));
            });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExchangeCancellationStopsBlockedIoAndTheChildProcess(bool write, bool timeout)
    {
        await WithCommandAsync(
            $"""
            @echo off
            "{ProcessFixturePath}" hold "%~1"
            """,
            async (execution, directory) =>
            {
                var pidPath = directory.FilePath("child.pid");
                var ioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var elapsed = Stopwatch.StartNew();
                var events = new ConcurrentQueue<string>();
                void Record(string message) => events.Enqueue($"{elapsed.Elapsed.TotalMilliseconds:F1} ms: {message}");

                Record($"Start at {DateTimeOffset.UtcNow:O}; write={write}; timeout={timeout}");
                Process? child = null;
                ICodexLineExchange? usedLines = null;
                Exception? testFailure = null;
                using var cancellation = new CancellationTokenSource();
                Record($"Launching command; exchange deadline is {(timeout ? 5 : 30)} seconds");
                var exchange = execution.ExchangeLinesAsync(
                    $"\"{pidPath}\"",
                    timeout ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30),
                    async lines =>
                    {
                        usedLines = lines;
                        Record("Command launched; awaiting ready line");
                        Assert.Equal("ready", await lines.ReadLineAsync());
                        Record("Received ready line; reading child PID file");
                        child = Process.GetProcessById(int.Parse(
                            await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken),
                            System.Globalization.CultureInfo.InvariantCulture));
                        Record($"Read child PID {child.Id}; initiating {(write ? "write" : "read")}");
                        var pendingIo = write
                            ? lines.WriteLineAsync(new string('x', 1_000_000))
                            : lines.ReadLineAsync().AsTask();
                        Record($"I/O call returned task with status {pendingIo.Status}");
                        Assert.False(pendingIo.IsCompleted, "The fixture must leave I/O pending before cancellation.");
                        ioStarted.TrySetResult();
                        try
                        {
                            await pendingIo;
                            Record("I/O completed normally");
                        }
                        catch (Exception exception)
                        {
                            Record($"I/O ended with {exception.GetType().Name}: {exception.Message}");
                            throw;
                        }
                        return true;
                    },
                    cancellation.Token);

                try
                {
                    await WaitForIoStartedAsync(ioStarted.Task, exchange, TimeSpan.FromSeconds(10));
                    Record("Readiness wait completed");
                    Assert.NotNull(child);
                    if (!timeout)
                    {
                        Record("Requesting caller cancellation");
                        cancellation.Cancel();
                    }

                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => exchange.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                    Record("Observed exchange cancellation; awaiting child exit");
                    await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Record("Child exit confirmed");
                }
                catch (Exception exception)
                {
                    testFailure = exception;
                    Record($"Test failed: {exception}");
                    Record($"PID file exists={File.Exists(pidPath)}; temporary PID file exists={File.Exists(pidPath + ".tmp")}");
                    throw;
                }
                finally
                {
                    Record($"Cleanup starting; exchange={exchange.Status}; readiness={ioStarted.Task.Status}; last stderr={usedLines?.LastStandardErrorLine ?? "<none>"}");
                    try
                    {
                        cancellation.Cancel();
                        if (child is { HasExited: false })
                        {
                            Record("Cleanup is killing the child process tree");
                            child.Kill(entireProcessTree: true);
                        }

                        await exchange.WaitAsync(TimeSpan.FromSeconds(25));
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException)
                    {
                        // Observe the operation and let its bounded child exit even if an assertion fails.
                        Record($"Cleanup observed {exception.GetType().Name}: {exception.Message}");
                    }
                    catch (Exception exception) when (testFailure is not null)
                    {
                        // Keep the original failure, including its readiness stage and stack trace.
                        Record($"Additional cleanup failure: {exception}");
                    }
                    finally
                    {
                        child?.Dispose();
                        Record($"Cleanup finished; exchange={exchange.Status}; last stderr={usedLines?.LastStandardErrorLine ?? "<none>"}");
                        foreach (var entry in events)
                        {
                            output.WriteLine($"[process-readiness] {entry}");
                        }
                    }
                }
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitedCommandWithInheritedPipesDoesNotBlockCompletion(bool capture)
    {
        await WithCommandAsync(
            $"""
            @echo off
            start "" /b "{ProcessFixturePath}" hold "%~1"
            """,
            async (execution, directory) =>
            {
                var pidPath = directory.FilePath("child.pid");
                using var cancellation = new CancellationTokenSource();
                Task operation = capture
                    ? execution.CaptureAsync($"\"{pidPath}\"", TimeSpan.FromSeconds(5), cancellation.Token)
                    : execution.ExchangeLinesAsync(
                        $"\"{pidPath}\"",
                        TimeSpan.FromSeconds(10),
                        async lines =>
                        {
                            Assert.Equal("ready", await lines.ReadLineAsync());
                            return true;
                        },
                        cancellation.Token);
                Process? child = null;
                try
                {
                    await WaitForFileAsync(pidPath);
                    child = Process.GetProcessById(int.Parse(
                        await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken),
                        System.Globalization.CultureInfo.InvariantCulture));
                    if (capture)
                    {
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                    }
                    else
                    {
                        await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    }
                }
                finally
                {
                    cancellation.Cancel();
                    if (child is { HasExited: false })
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }

                    child?.Dispose();
                    try
                    {
                        await operation.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (OperationCanceledException)
                    {
                        // Observe canceled capture after the fixture's descendant has stopped.
                    }
                }
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCanceledOperationDoesNotLaunchTheCommand(bool exchangeLines)
    {
        await WithCommandAsync(
            """
            @echo off
            > "%~1" echo launched
            """,
            async (execution, directory) =>
            {
                var markerPath = directory.FilePath("launched");
                var cancellationToken = new CancellationToken(canceled: true);
                var previousInterpreter = Environment.GetEnvironmentVariable("ComSpec");
                try
                {
                    // A launch attempt must fail even if the child would be killed
                    // before it could write the marker.
                    Environment.SetEnvironmentVariable("ComSpec", directory.FilePath("missing-interpreter.exe"));
                    Task operation = exchangeLines
                        ? execution.ExchangeLinesAsync<bool>(
                            $"\"{markerPath}\"",
                            TimeSpan.FromSeconds(5),
                            _ => throw new InvalidOperationException("A canceled exchange must not run."),
                            cancellationToken)
                        : execution.CaptureAsync(
                            $"\"{markerPath}\"",
                            TimeSpan.FromSeconds(5),
                            cancellationToken);

                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                    Assert.False(File.Exists(markerPath));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ComSpec", previousInterpreter);
                }
            });
    }

    [Fact]
    public async Task ReadinessWaitReportsAnEarlyExchangeFailure()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Child failed before readiness.");
        var exchange = Task.FromException(failure);

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            WaitForIoStartedAsync(ready.Task, exchange, TimeSpan.FromMilliseconds(100)));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task ReadinessWaitReportsAnEarlyExchangeCancellation()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = Task.FromCanceled(new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WaitForIoStartedAsync(ready.Task, exchange, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ReadinessWaitRejectsAnExchangeThatCompletesWithoutReadiness()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForIoStartedAsync(ready.Task, Task.CompletedTask, TimeSpan.FromMilliseconds(100)));

        Assert.Equal("The exchange completed before blocked I/O started.", failure.Message);
    }

    private static async Task WaitForIoStartedAsync(Task ioStarted, Task exchange, TimeSpan timeout)
    {
        await Task.WhenAny(ioStarted, exchange).WaitAsync(timeout, TestContext.Current.CancellationToken);
        if (!ioStarted.IsCompleted)
        {
            await exchange;
            throw new InvalidOperationException("The exchange completed before blocked I/O started.");
        }

        await ioStarted;
    }

    private static async Task WithCommandAsync(
        string command,
        Func<WindowsCodexProcessExecution, TemporaryDirectory, Task> test)
    {
        var previousPath = Environment.GetEnvironmentVariable("CODEX_USAGE_CODEX_PATH");
        using var directory = new TemporaryDirectory("codex-command");
        var commandPath = directory.FilePath("codex.cmd");
        try
        {
            await File.WriteAllTextAsync(commandPath, command, new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("CODEX_USAGE_CODEX_PATH", commandPath);
            await test(new WindowsCodexProcessExecution(), directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_USAGE_CODEX_PATH", previousPath);
        }
    }

    private static Task WithCommandAsync(
        string command,
        Func<WindowsCodexProcessExecution, Task> test) =>
        WithCommandAsync(command, (execution, _) => test(execution));

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class ProcessEnvironmentIsolation;
