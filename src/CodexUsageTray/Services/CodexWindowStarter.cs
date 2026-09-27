using System.Text.Json;
using static CodexUsageTray.CodexAppServerProtocol;

namespace CodexUsageTray;

internal sealed class CodexWindowStarter : IAllowanceWindowActivationCommand
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private readonly ICodexProcessExecution processExecution;

    internal CodexWindowStarter(ICodexProcessExecution processExecution)
    {
        this.processExecution = processExecution;
    }

    public async Task SendHiAsync(string? expectedAccountEmail, CancellationToken cancellationToken)
    {
        try
        {
            await processExecution.ExchangeLinesAsync(
                RequestTimeout,
                async lines =>
                {
                    await InitializeAsync(lines).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(expectedAccountEmail))
                    {
                        await SendAsync(lines, new
                        {
                            id = 5,
                            method = "account/read",
                            @params = new { refreshToken = false }
                        }).ConfigureAwait(false);
                        var account = await ReadResponseAsync(lines, 5).ConfigureAwait(false);
                        var email = account.TryGetProperty("result", out var result)
                            && result.TryGetProperty("account", out var current)
                            && current.ValueKind == JsonValueKind.Object
                            && current.TryGetProperty("email", out var value)
                            && value.ValueKind == JsonValueKind.String
                                ? value.GetString()
                                : null;
                        if (!string.Equals(email, expectedAccountEmail, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Codex account changed before allowance activation.");
                        }
                    }

                    var model = await SelectActivationModelAsync(lines).ConfigureAwait(false);
                    await SendAsync(lines, new
                    {
                        id = 2,
                        method = "thread/start",
                        @params = new
                        {
                            model,
                            cwd = Path.GetTempPath(),
                            approvalPolicy = "never",
                            sandbox = "read-only",
                            ephemeral = true,
                            serviceName = "codex-usage-tray",
                            developerInstructions = "Reply briefly to the greeting. Do not use tools."
                        }
                    }).ConfigureAwait(false);
                    var started = await ReadResponseAsync(lines, 2).ConfigureAwait(false);
                    if (!started.TryGetProperty("result", out var startResult)
                        || !startResult.TryGetProperty("thread", out var thread)
                        || !thread.TryGetProperty("id", out var threadIdElement)
                        || string.IsNullOrWhiteSpace(threadIdElement.GetString())
                        || !thread.TryGetProperty("ephemeral", out var ephemeral)
                        || ephemeral.ValueKind != JsonValueKind.True)
                    {
                        throw new InvalidOperationException("Codex did not start an ephemeral activation thread.");
                    }

                    var threadId = threadIdElement.GetString()!;
                    await SendAsync(lines, new
                    {
                        id = 3,
                        method = "turn/start",
                        @params = new
                        {
                            threadId,
                            input = new[] { new { type = "text", text = "Hi" } }
                        }
                    }).ConfigureAwait(false);
                    await WaitForCompletedTurnAsync(lines, threadId).ConfigureAwait(false);

                    await SendAsync(lines, new
                    {
                        id = 4,
                        method = "thread/unsubscribe",
                        @params = new { threadId }
                    }).ConfigureAwait(false);
                    await ReadResponseAsync(lines, 4).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The automatic Codex request did not finish within two minutes.");
        }
    }

    private static async Task<string> SelectActivationModelAsync(ICodexLineExchange lines)
    {
        string? cursor = null;
        string? olderLuna = null;
        string? defaultModel = null;
        do
        {
            var parameters = new Dictionary<string, object>
            {
                ["limit"] = 100,
                ["includeHidden"] = false
            };
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }
            await SendAsync(lines, new { id = 6, method = "model/list", @params = parameters })
                .ConfigureAwait(false);
            var response = await ReadResponseAsync(lines, 6).ConfigureAwait(false);
            if (!response.TryGetProperty("result", out var result)
                || !result.TryGetProperty("data", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Codex did not return available activation models.");
            }

            foreach (var candidate in models.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object
                    || !candidate.TryGetProperty("model", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    continue;
                }

                var model = value.GetString()!;
                if (model == "gpt-6-luna")
                {
                    return model;
                }
                if (model == "gpt-5.6-luna")
                {
                    olderLuna = model;
                }
                if (candidate.TryGetProperty("isDefault", out var isDefault)
                    && isDefault.ValueKind == JsonValueKind.True)
                {
                    defaultModel = model;
                }
            }

            cursor = result.TryGetProperty("nextCursor", out var next)
                && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        return olderLuna ?? defaultModel
            ?? throw new InvalidOperationException("Codex did not offer an activation model.");
    }

    private static async Task WaitForCompletedTurnAsync(ICodexLineExchange lines, string threadId)
    {
        string? turnId = null;
        JsonElement? completed = null;
        while (true)
        {
            var message = await ReadNextMessageAsync(lines).ConfigureAwait(false);
            if (message.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var responseId)
                && responseId == 3)
            {
                ThrowIfProtocolError(message);
                if (!message.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("turn", out var turn)
                    || !turn.TryGetProperty("id", out var turnIdElement)
                    || string.IsNullOrWhiteSpace(turnIdElement.GetString()))
                {
                    throw new InvalidOperationException("Codex did not return an activation turn.");
                }

                turnId = turnIdElement.GetString();
                if (turn.TryGetProperty("status", out var status)
                    && status.GetString() != "inProgress")
                {
                    completed = turn;
                }
            }
            else if (message.TryGetProperty("method", out var method)
                && method.GetString() == "turn/completed"
                && message.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("threadId", out var completedThreadId)
                && completedThreadId.GetString() == threadId
                && parameters.TryGetProperty("turn", out var turn))
            {
                completed = turn;
            }

            if (turnId is null || completed is not { } finished
                || !finished.TryGetProperty("id", out var completedTurnId)
                || completedTurnId.GetString() != turnId)
            {
                continue;
            }

            var finalStatus = finished.GetProperty("status").GetString();
            if (finalStatus == "completed")
            {
                return;
            }

            var detail = finished.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : finalStatus;
            throw new InvalidOperationException($"The automatic Codex request failed: {detail}");
        }
    }
}
