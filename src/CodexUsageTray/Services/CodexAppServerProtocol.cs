using System.Text.Json;

namespace CodexUsageTray;

internal static class CodexAppServerProtocol
{
    private const string ExpiredAuthenticationError = "Provided authentication token is expired";
    private static readonly string ClientVersion = typeof(CodexAppServerProtocol).Assembly
        .GetName().Version?.ToString(3) ?? "unknown";

    internal static async Task InitializeAsync(ICodexLineExchange lines)
    {
        await SendAsync(lines, new
        {
            id = 1,
            method = "initialize",
            @params = new
            {
                clientInfo = new
                {
                    name = "codex-usage-tray",
                    title = "Codex Usage Tray",
                    version = ClientVersion
                },
                capabilities = new { experimentalApi = true }
            }
        }).ConfigureAwait(false);

        await ReadResponseAsync(lines, 1).ConfigureAwait(false);
        await SendAsync(lines, new { method = "initialized" }).ConfigureAwait(false);
    }

    internal static Task SendAsync(ICodexLineExchange lines, object message) =>
        lines.WriteLineAsync(JsonSerializer.Serialize(message));

    internal static async Task<JsonElement> ReadResponseAsync(
        ICodexLineExchange lines,
        int expectedId)
    {
        while (true)
        {
            var response = await ReadNextMessageAsync(lines).ConfigureAwait(false);
            if (response.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var number)
                && number == expectedId)
            {
                ThrowIfProtocolError(response);
                return response;
            }
        }
    }

    internal static async Task<JsonElement> ReadNextMessageAsync(ICodexLineExchange lines)
    {
        var line = await lines.ReadLineAsync().ConfigureAwait(false);
        if (line is null)
        {
            throw new InvalidOperationException("The Codex app-server closed before returning usage data.");
        }

        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    internal static void ThrowIfProtocolError(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error))
        {
            return;
        }

        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : error.ToString();
        if (message?.Contains(ExpiredAuthenticationError, StringComparison.OrdinalIgnoreCase) is true)
        {
            throw new CodexAuthenticationExpiredException();
        }

        throw new InvalidOperationException($"Codex returned an error: {message}");
    }
}

internal sealed class CodexAuthenticationExpiredException : InvalidOperationException;
