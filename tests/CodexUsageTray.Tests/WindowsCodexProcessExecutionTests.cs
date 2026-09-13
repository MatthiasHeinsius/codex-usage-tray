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
            1>&2 echo diagnostic
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

    [Fact]
    public async Task CaptureCancellationStopsTheCommandProcess()
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
                    TimeSpan.FromSeconds(5),
                    cancellation.Token);
                await WaitForFileAsync(markerPath + ".started");

                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
                await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                Assert.False(File.Exists(markerPath + ".finished"));
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
