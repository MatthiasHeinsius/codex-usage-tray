namespace CodexUsageTray;

internal sealed class CodexWindowStarter : IAllowanceWindowActivationCommand
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private const string Arguments =
        "exec --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules "
        + "-m gpt-5.6-luna -s read-only --color never \"Hi\"";
    private readonly ICodexProcessExecution processExecution;

    internal CodexWindowStarter(ICodexProcessExecution processExecution)
    {
        this.processExecution = processExecution;
    }

    public async Task SendHiAsync(CancellationToken cancellationToken)
    {
        CodexProcessOutput output;
        try
        {
            output = await processExecution.CaptureAsync(Arguments, RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The automatic Codex request did not finish within two minutes.");
        }

        if (output.ExitCode != 0)
        {
            var detail = LastUsefulLine(output.StandardError)
                ?? LastUsefulLine(output.StandardOutput)
                ?? $"exit code {output.ExitCode}";
            throw new InvalidOperationException($"The automatic Codex request failed: {detail}");
        }
    }

    private static string? LastUsefulLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault();
}
