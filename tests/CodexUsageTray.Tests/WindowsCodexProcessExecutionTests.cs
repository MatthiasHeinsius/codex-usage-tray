using System.Diagnostics;
using System.Text;

namespace CodexUsageTray.Tests;

[Collection<ProcessEnvironmentIsolation>]
public sealed class WindowsCodexProcessExecutionTests
{
    private static string ProcessFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "CodexUsageTray.ProcessFixture.exe");

    [Fact]
    public async Task AppServerReusesConnectionAndRestartsAfterTimeoutOrExternalLogin()
    {
        await WithCommandAsync(
            $"""
            @echo off
            "{ProcessFixturePath}" app-server "%~dp0retry.marker"
            """,
            async (execution, directory) =>
            {
                var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                Environment.SetEnvironmentVariable("CODEX_HOME", directory.RootPath);
                try
                {
                    async Task<int> ReadPidAsync() => await execution.ExchangeReadOnlyLinesAsync(
                        TimeSpan.FromSeconds(5),
                        async lines =>
                        {
                            await CodexAppServerProtocol.InitializeAsync(lines);
                            await CodexAppServerProtocol.SendAsync(lines, new { id = 2, method = "pid" });
                            var response = await CodexAppServerProtocol.ReadResponseAsync(lines, 2);
                            return response.GetProperty("result").GetProperty("pid").GetInt32();
                        },
                        TestContext.Current.CancellationToken);

                    var first = await ReadPidAsync();
                    Assert.Equal(first, await ReadPidAsync());
                    await new CodexWindowStarter(execution).SendHiAsync(null, TestContext.Current.CancellationToken);
                    Assert.Equal(first, await ReadPidAsync());

                    execution.RequestReconnect();
                    var afterReconnect = await ReadPidAsync();
                    Assert.NotEqual(first, afterReconnect);
                    Assert.Equal(afterReconnect, await ReadPidAsync());

                    await File.WriteAllTextAsync(directory.FilePath("auth.json"), "account changed");
                    var afterLogin = await ReadPidAsync();
                    Assert.NotEqual(afterReconnect, afterLogin);
                    Assert.Equal(afterLogin, await ReadPidAsync());

                    await Assert.ThrowsAsync<CodexAuthenticationExpiredException>(() =>
                        execution.ExchangeLinesAsync(
                            TimeSpan.FromSeconds(5),
                            async lines =>
                            {
                                await CodexAppServerProtocol.SendAsync(lines, new { id = 3, method = "auth-failure" });
                                return await CodexAppServerProtocol.ReadResponseAsync(lines, 3);
                            },
                            TestContext.Current.CancellationToken));
                    var afterAuthFailure = await ReadPidAsync();
                    Assert.NotEqual(afterLogin, afterAuthFailure);

                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        execution.ExchangeLinesAsync(
                            TimeSpan.FromMilliseconds(300),
                            async lines =>
                            {
                                await CodexAppServerProtocol.SendAsync(lines, new { id = 3, method = "hang" });
                                return await CodexAppServerProtocol.ReadResponseAsync(lines, 3);
                            },
                            CancellationToken.None));
                    Assert.NotEqual(afterAuthFailure, await ReadPidAsync());

                    var recovered = await execution.ExchangeReadOnlyLinesAsync(
                        TimeSpan.FromSeconds(5),
                        async lines =>
                        {
                            await CodexAppServerProtocol.InitializeAsync(lines);
                            await CodexAppServerProtocol.SendAsync(lines, new { id = 4, method = "fail-once" });
                            var response = await CodexAppServerProtocol.ReadResponseAsync(lines, 4);
                            return response.GetProperty("result").GetProperty("pid").GetInt32();
                        },
                        TestContext.Current.CancellationToken);
                    Assert.Equal(recovered, await ReadPidAsync());

                    await execution.ExchangeLinesAsync(
                        TimeSpan.FromSeconds(5),
                        lines =>
                        {
                            lines.DiscardConnection();
                            return Task.FromResult(true);
                        },
                        TestContext.Current.CancellationToken);
                    Assert.NotEqual(recovered, await ReadPidAsync());
                }
                finally
                {
                    Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
                }
            });
    }

    [Fact]
    public async Task AppServerRejectsAnOversizedProtocolLine()
    {
        await WithCommandAsync(
            $"""
            @echo off
            "{ProcessFixturePath}" app-server "%~dp0retry.marker"
            """,
            async (execution, _) =>
            {
                var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    execution.ExchangeLinesAsync(
                        TimeSpan.FromSeconds(20),
                        async lines =>
                        {
                            await CodexAppServerProtocol.InitializeAsync(lines);
                            await CodexAppServerProtocol.SendAsync(lines, new { id = 2, method = "oversized-line" });
                            return await lines.ReadLineAsync();
                        },
                        TestContext.Current.CancellationToken));

                Assert.Contains("oversized", failure.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task AppServerKeepsOnlyTheTailOfLongDiagnosticLines()
    {
        await WithCommandAsync(
            $"""
            @echo off
            "{ProcessFixturePath}" app-server "%~dp0retry.marker"
            """,
            async (execution, _) =>
            {
                await execution.ExchangeLinesAsync(
                    TimeSpan.FromSeconds(5),
                    async lines =>
                    {
                        await CodexAppServerProtocol.InitializeAsync(lines);
                        await CodexAppServerProtocol.SendAsync(lines, new { id = 2, method = "long-diagnostic" });
                        await CodexAppServerProtocol.ReadResponseAsync(lines, 2);
                        for (var attempt = 0; attempt < 50 && lines.LastStandardErrorLine is null; attempt++)
                        {
                            await Task.Delay(10, TestContext.Current.CancellationToken);
                        }

                        Assert.Equal(4096, lines.LastStandardErrorLine?.Length);
                        Assert.EndsWith("end", lines.LastStandardErrorLine, StringComparison.Ordinal);
                        return true;
                    },
                    TestContext.Current.CancellationToken);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrTimeoutStopsBlockedAppServerAndChild(bool timeout)
    {
        await WithCommandAsync(
            $"""
            @echo off
            "{ProcessFixturePath}" hold "%~dp0child.pid"
            """,
            async (execution, directory) =>
            {
                var pidPath = directory.FilePath("child.pid");
                using var cancellation = new CancellationTokenSource();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var exchange = execution.ExchangeLinesAsync(
                    timeout ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10),
                    async lines =>
                    {
                        Assert.Equal("ready", await lines.ReadLineAsync());
                        ready.SetResult();
                        return await lines.ReadLineAsync();
                    },
                    cancellation.Token);

                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                var pid = int.Parse(
                    await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                using var child = Process.GetProcessById(pid);
                if (!timeout)
                {
                    cancellation.Cancel();
                }

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            });
    }

    [Fact]
    public async Task AlreadyCanceledExchangeDoesNotStartAppServer()
    {
        await WithCommandAsync(
            """
            @echo off
            > "%~dp0started" echo started
            """,
            async (execution, directory) =>
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    execution.ExchangeLinesAsync<bool>(
                        TimeSpan.FromSeconds(5),
                        _ => throw new InvalidOperationException("A canceled exchange must not run."),
                        new CancellationToken(canceled: true)));
                Assert.False(File.Exists(directory.FilePath("started")));
            });
    }

    private static async Task WithCommandAsync(
        string command,
        Func<WindowsCodexProcessExecution, TemporaryDirectory, Task> test)
    {
        var previousPath = Environment.GetEnvironmentVariable("CODEX_USAGE_CODEX_PATH");
        using var directory = new TemporaryDirectory("codex-command");
        try
        {
            await File.WriteAllTextAsync(directory.FilePath("codex.cmd"), command, new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("CODEX_USAGE_CODEX_PATH", directory.FilePath("codex.cmd"));
            await using var execution = new WindowsCodexProcessExecution();
            await test(execution, directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_USAGE_CODEX_PATH", previousPath);
        }
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class ProcessEnvironmentIsolation;
