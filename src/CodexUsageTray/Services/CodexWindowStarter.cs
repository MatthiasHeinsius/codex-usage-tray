using System.Diagnostics;

namespace CodexUsageTray;

internal sealed class CodexWindowStarter : IAllowanceWindowActivationCommand
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    public async Task SendHiAsync(CancellationToken cancellationToken)
    {
        var codexPath = CodexCommandLocator.Find();
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /s /c \"\"{codexPath}\" exec --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules -m gpt-5.6-luna -s read-only --color never \"Hi\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the Codex CLI.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryStop(process);
            throw new InvalidOperationException("The automatic Codex request did not finish within two minutes.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            var detail = LastUsefulLine(error) ?? LastUsefulLine(output) ?? $"exit code {process.ExitCode}";
            throw new InvalidOperationException($"The automatic Codex request failed: {detail}");
        }
    }

    private static string? LastUsefulLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault();

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }
}
