using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed class CodexAppServerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly string ClientVersion = typeof(CodexAppServerClient).Assembly
        .GetName().Version?.ToString(3) ?? "unknown";
    private readonly LocalTokenUsageReader localTokenUsage = new();

    public Task<UsageObservations> ReadUsageAsync(CancellationToken cancellationToken) =>
        ReadUsageAsync(includeActivity: true, cancellationToken);

    public Task<UsageObservations> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
        ReadUsageAsync(includeActivity: false, cancellationToken);

    private async Task<UsageObservations> ReadUsageAsync(bool includeActivity, CancellationToken cancellationToken)
    {
        var codexPath = CodexCommandLocator.Find();
        using var process = StartAppServer(codexPath);
        var errors = new List<string>();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                lock (errors)
                {
                    errors.Add(eventArgs.Data);
                }
            }
        };
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            await SendAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "codex-usage-tray", title = "Codex Usage Tray", version = ClientVersion },
                    capabilities = new { experimentalApi = true }
                }
            });

            await ReadResponseAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized" });
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read", @params = (object?)null });
            if (includeActivity)
            {
                await SendAsync(process, new { id = 3, method = "account/usage/read", @params = (object?)null });
            }

            JsonElement? rateLimits = null;
            JsonElement? tokenUsage = null;

            while (rateLimits is null || (includeActivity && tokenUsage is null))
            {
                var response = await ReadNextMessageAsync(process, timeout.Token);
                if (!response.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
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
                        local = new LocalUsageObservation(DateOnly.FromDateTime(now.LocalDateTime), tokens);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string detail;
            lock (errors)
            {
                detail = errors.LastOrDefault() ?? "No diagnostic message was returned.";
            }

            throw new InvalidOperationException($"Codex did not return usage data within 45 seconds. {detail}");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            string detail;
            lock (errors)
            {
                detail = errors.LastOrDefault() ?? exception.Message;
            }

            throw new InvalidOperationException($"Could not read Codex usage: {detail}", exception);
        }
        finally
        {
            TryStop(process);
        }
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

        destination.Add(new AllowanceWindow(Math.Clamp(used, 0, 100), durationValue, reset));
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Process StartAppServer(string codexPath)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /s /c \"\"{codexPath}\" app-server --stdio\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the Codex CLI.");
        return process;
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(Process process, int expectedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await ReadNextMessageAsync(process, cancellationToken);
            if (response.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number == expectedId)
            {
                ThrowIfProtocolError(response);
                return response;
            }
        }
    }

    private static async Task<JsonElement> ReadNextMessageAsync(Process process, CancellationToken cancellationToken)
    {
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
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

    private static void TryStop(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(500))
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
