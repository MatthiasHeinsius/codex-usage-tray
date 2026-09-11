namespace CodexUsageTray.Tests;

public sealed class CodexWindowStarterTests
{
    private const string ExpectedArguments =
        "exec --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules "
        + "-m gpt-5.6-luna -s read-only --color never \"Hi\"";

    [Fact]
    public async Task SendHiUsesCapturedProcessExecution()
    {
        var processes = new ScriptedCodexProcessExecution
        {
            CapturedOutput = new CodexProcessOutput(0, "completed", string.Empty)
        };
        var starter = new CodexWindowStarter(processes);

        await starter.SendHiAsync(CancellationToken.None);

        Assert.Equal(ExpectedArguments, processes.CaptureArguments);
        Assert.Equal(TimeSpan.FromMinutes(2), processes.CaptureTimeout);
        Assert.Equal(CancellationToken.None, processes.CaptureCancellationToken);
    }

    [Fact]
    public async Task SendHiReportsTheLastStandardErrorLine()
    {
        var processes = new ScriptedCodexProcessExecution
        {
            CapturedOutput = new CodexProcessOutput(7, "output detail", "first error\r\nlast error\r\n")
        };
        var starter = new CodexWindowStarter(processes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => starter.SendHiAsync(CancellationToken.None));

        Assert.Equal("The automatic Codex request failed: last error", failure.Message);
    }

    [Fact]
    public async Task SendHiMapsProcessTimeout()
    {
        var processes = new ScriptedCodexProcessExecution
        {
            CaptureFailure = new OperationCanceledException()
        };
        var starter = new CodexWindowStarter(processes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => starter.SendHiAsync(CancellationToken.None));

        Assert.Equal("The automatic Codex request did not finish within two minutes.", failure.Message);
    }

    [Fact]
    public async Task SendHiPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var processes = new ScriptedCodexProcessExecution
        {
            CaptureFailure = new OperationCanceledException(cancellation.Token)
        };
        var starter = new CodexWindowStarter(processes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => starter.SendHiAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, processes.CaptureCancellationToken);
    }
}
