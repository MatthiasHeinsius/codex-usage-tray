using System.Text.Json;

namespace CodexUsageTray;

internal sealed class CodexUsageObservationReader : IUsageObservationReader
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly string ClientVersion = typeof(CodexUsageObservationReader).Assembly
        .GetName().Version?.ToString(3) ?? "unknown";
    private readonly ICodexProcessExecution processExecution;
    private readonly LocalTokenUsageReader localTokenUsage = new();

    internal CodexUsageObservationReader(ICodexProcessExecution processExecution)
    {
        this.processExecution = processExecution;
    }

    public Task<UsageObservations> ReadAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken) =>
        ReadUsageAsync(request == UsageObservationRequest.AllowanceWindowsAndActivity, cancellationToken);

    private async Task<UsageObservations> ReadUsageAsync(bool includeActivity, CancellationToken cancellationToken)
    {
        return await processExecution.ExchangeLinesAsync(
            "app-server --stdio",
            RequestTimeout,
            async lines =>
            {
                try
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
                    await SendAsync(
                        lines,
                        new { id = 2, method = "account/rateLimits/read", @params = (object?)null })
                        .ConfigureAwait(false);
                    if (includeActivity)
                    {
                        await SendAsync(
                            lines,
                            new { id = 3, method = "account/usage/read", @params = (object?)null })
                            .ConfigureAwait(false);
                    }

                    JsonElement? rateLimits = null;
                    JsonElement? tokenUsage = null;

                    while (rateLimits is null || (includeActivity && tokenUsage is null))
                    {
                        var response = await ReadNextMessageAsync(lines).ConfigureAwait(false);
                        if (!response.TryGetProperty("id", out var idElement)
                            || !idElement.TryGetInt32(out var id))
                        {
                            continue;
                        }

                        ThrowIfProtocolError(response);
                        if (!response.TryGetProperty("result", out var result))
                        {
                            continue;
                        }

                        if (id == 2)
                        {
                            rateLimits = result.Clone();
                        }
                        else if (id == 3)
                        {
                            tokenUsage = result.Clone();
                        }
                    }

                    var now = DateTimeOffset.Now;
                    var account = ParseAccountObservation(rateLimits.Value, tokenUsage, now);
                    LocalUsageObservation? local = null;
                    if (account.Activity is AccountActivityObservation.Observed { TodayTokens: null })
                    {
                        try
                        {
                            var localToday = localTokenUsage.ReadToday(now);
                            if (localToday is { } tokens)
                            {
                                local = new LocalUsageObservation(
                                    DateOnly.FromDateTime(now.LocalDateTime),
                                    tokens);
                            }
                        }
                        catch (IOException)
                        {
                            // A Codex session file may be rotating. Try again on the next refresh.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Local session history is an optional fallback.
                        }
                    }

                    return new UsageObservations(account, local);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    var detail = lines.LastStandardErrorLine ?? "No diagnostic message was returned.";
                    throw new InvalidOperationException(
                        $"Codex did not return usage data within 45 seconds. {detail}");
                }
                catch (Exception exception) when (exception is not InvalidOperationException)
                {
                    var detail = lines.LastStandardErrorLine ?? exception.Message;
                    throw new InvalidOperationException($"Could not read Codex usage: {detail}", exception);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static AccountUsageObservation ParseAccountObservation(
        JsonElement rateResponse,
        JsonElement? usageResponse,
        DateTimeOffset now)
    {
        var limits = SelectCodexLimits(rateResponse);
        var windows = new List<AllowanceWindow>();
        AddWindow(limits, "primary", windows);
        AddWindow(limits, "secondary", windows);

        long? lifetimeTokens = null;
        if (usageResponse is { } activity
            && activity.TryGetProperty("summary", out var summary)
            && summary.TryGetProperty("lifetimeTokens", out var lifetime)
            && lifetime.ValueKind == JsonValueKind.Number
            && lifetime.TryGetInt64(out var lifetimeValue))
        {
            lifetimeTokens = lifetimeValue;
        }

        long? todayTokens = null;
        if (usageResponse is { } dailyActivity
            && dailyActivity.TryGetProperty("dailyUsageBuckets", out var buckets)
            && buckets.ValueKind == JsonValueKind.Array)
        {
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.TryGetProperty("startDate", out var dateElement)
                    && DateOnly.TryParse(dateElement.GetString(), out var date)
                    && date == today
                    && bucket.TryGetProperty("tokens", out var tokensElement)
                    && tokensElement.TryGetInt64(out var value))
                {
                    todayTokens = value;
                    break;
                }
            }
        }

        var activityObservation = usageResponse is null
            ? (AccountActivityObservation)new AccountActivityObservation.NotRequested()
            : new AccountActivityObservation.Observed(
                lifetimeTokens,
                todayTokens,
                LatestDailyBucketDate(usageResponse.Value));

        return new AccountUsageObservation(
            now,
            windows,
            GetString(limits, "planType"),
            GetString(limits, "limitName"),
            activityObservation);
    }

    private static DateOnly? LatestDailyBucketDate(JsonElement usageResponse)
    {
        if (!usageResponse.TryGetProperty("dailyUsageBuckets", out var buckets)
            || buckets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        DateOnly? latest = null;
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.TryGetProperty("startDate", out var dateElement)
                && DateOnly.TryParse(dateElement.GetString(), out var date)
                && (latest is null || date > latest.Value))
            {
                latest = date;
            }
        }

        return latest;
    }

    private static JsonElement SelectCodexLimits(JsonElement response)
    {
        if (response.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            if (byId.TryGetProperty("codex", out var codex))
            {
                return codex;
            }

            foreach (var property in byId.EnumerateObject())
            {
                if (property.Name.Contains("codex", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
        }

        if (!response.TryGetProperty("rateLimits", out var limits))
        {
            throw new InvalidOperationException("Codex returned no rate-limit snapshot.");
        }

        return limits;
    }

    private static void AddWindow(JsonElement limits, string name, List<AllowanceWindow> destination)
    {
        if (!limits.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!element.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetInt32(out var used))
        {
            return;
        }

        TimeSpan? durationValue = null;
        if (element.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt32(out var durationMinutes))
        {
            durationValue = TimeSpan.FromMinutes(durationMinutes);
        }

        DateTimeOffset? reset = null;
        if (element.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unixSeconds))
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        destination.Add(new AllowanceWindow(used, durationValue, reset));
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Task SendAsync(ICodexLineExchange lines, object message) =>
        lines.WriteLineAsync(JsonSerializer.Serialize(message));

    private static async Task<JsonElement> ReadResponseAsync(
        ICodexLineExchange lines,
        int expectedId)
    {
        while (true)
        {
            var response = await ReadNextMessageAsync(lines).ConfigureAwait(false);
            if (response.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number == expectedId)
            {
                ThrowIfProtocolError(response);
                return response;
            }
        }
    }

    private static async Task<JsonElement> ReadNextMessageAsync(ICodexLineExchange lines)
    {
        var line = await lines.ReadLineAsync().ConfigureAwait(false);
        if (line is null)
        {
            throw new InvalidOperationException("The Codex app-server closed before returning usage data.");
        }

        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static void ThrowIfProtocolError(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error))
        {
            return;
        }

        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : error.ToString();
        throw new InvalidOperationException($"Codex returned an error: {message}");
    }

}
