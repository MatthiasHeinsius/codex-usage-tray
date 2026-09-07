using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed class CodexAppServerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly LocalTokenUsageReader localTokenUsage = new();

    public async Task<UsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken)
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
                    clientInfo = new { name = "codex-usage-tray", title = "Codex Usage Tray", version = "1.1.0" },
                    capabilities = new { experimentalApi = true }
                }
            });

            await ReadResponseAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized" });
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read", @params = (object?)null });
            await SendAsync(process, new { id = 3, method = "account/usage/read", @params = (object?)null });

            JsonElement? rateLimits = null;
            JsonElement? tokenUsage = null;

            while (rateLimits is null || tokenUsage is null)
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
            var snapshot = ParseSnapshot(rateLimits.Value, tokenUsage.Value, now);
            if (snapshot.TodayTokens is null)
            {
                try
                {
                    var localToday = localTokenUsage.ReadToday(now);
                    if (localToday is not null)
                    {
                        snapshot = ApplyLocalTodayFallback(snapshot, tokenUsage.Value, localToday.Value, now);
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

            return snapshot;
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

    internal static UsageSnapshot ParseSnapshot(JsonElement rateResponse, JsonElement usageResponse, DateTimeOffset now)
    {
        var limits = SelectCodexLimits(rateResponse);
        var windows = new List<UsageWindow>();
        AddWindow(limits, "primary", windows);
        AddWindow(limits, "secondary", windows);

        var fiveHour = windows.FirstOrDefault(window => window.WindowMinutes is >= 240 and <= 360)
            ?? windows.OrderBy(window => window.WindowMinutes ?? int.MaxValue).FirstOrDefault();
        var weekly = windows.FirstOrDefault(window => window.WindowMinutes is >= 9_000 and <= 11_000)
            ?? windows.OrderByDescending(window => window.WindowMinutes ?? int.MinValue).FirstOrDefault(window => window != fiveHour);

        long? lifetimeTokens = null;
        if (usageResponse.TryGetProperty("summary", out var summary)
            && summary.TryGetProperty("lifetimeTokens", out var lifetime)
            && lifetime.ValueKind == JsonValueKind.Number
            && lifetime.TryGetInt64(out var lifetimeValue))
        {
            lifetimeTokens = lifetimeValue;
        }

        long? todayTokens = null;
        if (usageResponse.TryGetProperty("dailyUsageBuckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
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

        return new UsageSnapshot(
            now,
            fiveHour,
            weekly,
            lifetimeTokens,
            todayTokens,
            GetString(limits, "planType"),
            GetString(limits, "limitName"));
    }

    internal static UsageSnapshot ApplyLocalTodayFallback(
        UsageSnapshot snapshot,
        JsonElement usageResponse,
        long localTodayTokens,
        DateTimeOffset now)
    {
        var result = snapshot with
        {
            TodayTokens = localTodayTokens,
            TodayTokensAreLocal = true
        };

        if (snapshot.LifetimeTokens is not { } serverLifetime)
        {
            return result;
        }

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (LatestDailyBucketDate(usageResponse) != today.AddDays(-1))
        {
            return result;
        }

        try
        {
            return result with
            {
                LifetimeTokens = checked(serverLifetime + localTodayTokens),
                LifetimeIncludesLocalToday = true
            };
        }
        catch (OverflowException)
        {
            return result;
        }
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

    private static void AddWindow(JsonElement limits, string name, List<UsageWindow> destination)
    {
        if (!limits.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!element.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetInt32(out var used))
        {
            return;
        }

        int? minutes = null;
        if (element.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt32(out var durationValue))
        {
            minutes = durationValue;
        }

        DateTimeOffset? reset = null;
        if (element.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unixSeconds))
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        destination.Add(new UsageWindow(Math.Clamp(used, 0, 100), minutes, reset));
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
