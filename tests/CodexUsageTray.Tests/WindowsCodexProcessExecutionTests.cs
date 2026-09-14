using System.Diagnostics;
using System.Text;

namespace CodexUsageTray.Tests;

[Collection<ProcessEnvironmentIsolation>]
public sealed class WindowsCodexProcessExecutionTests
{
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
            """
            @echo off
            > "%~1.started" echo started
            powershell.exe -NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Milliseconds 800"
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
            """
            @echo off
            set "CODEX_USAGE_TEST_PID_PATH=%~1"
            powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$path = $env:CODEX_USAGE_TEST_PID_PATH; $PID | Set-Content -LiteralPath ($path + '.tmp'); Move-Item -LiteralPath ($path + '.tmp') -Destination $path; Write-Output 'ready'; Start-Sleep -Seconds 20"
            """,
            async (execution, directory) =>
            {
                var pidPath = directory.FilePath("child.pid");
                var ioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Process? child = null;
                using var cancellation = new CancellationTokenSource();
                var exchange = execution.ExchangeLinesAsync(
                    $"\"{pidPath}\"",
                    timeout ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30),
                    async lines =>
                    {
                        Assert.Equal("ready", await lines.ReadLineAsync());
                        child = Process.GetProcessById(int.Parse(
                            await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken),
                            System.Globalization.CultureInfo.InvariantCulture));
                        var pendingIo = write
                            ? lines.WriteLineAsync(new string('x', 1_000_000))
                            : lines.ReadLineAsync().AsTask();
                        ioStarted.TrySetResult();
                        await pendingIo;
                        return true;
                    },
                    cancellation.Token);

                try
                {
                    await ioStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    Assert.NotNull(child);
                    if (!timeout)
                    {
                        cancellation.Cancel();
                    }

                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => exchange.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                    await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                finally
                {
                    cancellation.Cancel();
                    try
                    {
                        if (child is { HasExited: false })
                        {
                            child.Kill(entireProcessTree: true);
                        }

                        await exchange.WaitAsync(TimeSpan.FromSeconds(25));
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException)
                    {
                        // Observe the operation and let its bounded child exit even if an assertion fails.
                    }
                    finally
                    {
                        child?.Dispose();
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
            """
            @echo off
            set "CODEX_USAGE_TEST_PID_PATH=%~1"
            start "" /b powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$path = $env:CODEX_USAGE_TEST_PID_PATH; $PID | Set-Content -LiteralPath ($path + '.tmp'); Move-Item -LiteralPath ($path + '.tmp') -Destination $path; Write-Output 'ready'; Start-Sleep -Seconds 20"
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
