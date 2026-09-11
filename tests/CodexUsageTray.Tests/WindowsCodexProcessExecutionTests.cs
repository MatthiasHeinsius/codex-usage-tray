using System.Text;

namespace CodexUsageTray.Tests;

public sealed class WindowsCodexProcessExecutionTests
{
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

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

    private static async Task WithCommandAsync(
        string command,
        Func<WindowsCodexProcessExecution, Task> test)
    {
        await EnvironmentLock.WaitAsync();
        var previousPath = Environment.GetEnvironmentVariable("CODEX_USAGE_CODEX_PATH");
        var directory = Path.Combine(Path.GetTempPath(), $"Codex Usage Tray Tests {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var commandPath = Path.Combine(directory, "codex.cmd");
        try
        {
            await File.WriteAllTextAsync(commandPath, command, new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("CODEX_USAGE_CODEX_PATH", commandPath);
            await test(new WindowsCodexProcessExecution());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_USAGE_CODEX_PATH", previousPath);
            Directory.Delete(directory, recursive: true);
            EnvironmentLock.Release();
        }
    }
}
