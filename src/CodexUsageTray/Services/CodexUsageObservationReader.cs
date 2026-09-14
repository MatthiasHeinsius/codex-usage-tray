using System.Text.Json;
using static CodexUsageTray.CodexAppServerProtocol;

namespace CodexUsageTray;

internal sealed class CodexUsageObservationReader : IUsageObservationReader
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly ICodexProcessExecution processExecution;
    private readonly ILocalTokenUsageReader localTokenUsage;
    private readonly TimeProvider timeProvider;
    private readonly CodexAuthenticationRecovery authenticationRecovery;

    internal CodexUsageObservationReader(ICodexProcessExecution processExecution)
        : this(
            processExecution,
            new LocalTokenUsageReader(),
            TimeProvider.System,
            new CodexAuthenticationRecovery(processExecution, interaction: null))
    {
    }

    internal CodexUsageObservationReader(
        ICodexProcessExecution processExecution,
        ICodexAuthenticationInteraction authenticationInteraction)
        : this(
            processExecution,
            new LocalTokenUsageReader(),
            TimeProvider.System,
            new CodexAuthenticationRecovery(processExecution, authenticationInteraction))
    {
    }

    internal CodexUsageObservationReader(
        ICodexProcessExecution processExecution,
        ILocalTokenUsageReader localTokenUsage,
        TimeProvider timeProvider)
        : this(
            processExecution,
            localTokenUsage,
            timeProvider,
            new CodexAuthenticationRecovery(processExecution, interaction: null))
    {
    }

    internal CodexUsageObservationReader(
        ICodexProcessExecution processExecution,
        ILocalTokenUsageReader localTokenUsage,
        TimeProvider timeProvider,
        CodexAuthenticationRecovery authenticationRecovery)
    {
        ArgumentNullException.ThrowIfNull(processExecution);
        ArgumentNullException.ThrowIfNull(localTokenUsage);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(authenticationRecovery);
        this.processExecution = processExecution;
        this.localTokenUsage = localTokenUsage;
        this.timeProvider = timeProvider;
        this.authenticationRecovery = authenticationRecovery;
    }

    public Task<UsageObservations> ReadAsync(
        UsageObservationRequest request,
        CancellationToken cancellationToken)
    {
        var includeActivity = request == UsageObservationRequest.AllowanceWindowsAndActivity;
        return authenticationRecovery.RunAsync(
            token => ReadUsageAsync(includeActivity, token),
            cancellationToken);
    }

    private async Task<UsageObservations> ReadUsageAsync(bool includeActivity, CancellationToken cancellationToken)
    {
        return await processExecution.ExchangeLinesAsync(
            "app-server --stdio",
            RequestTimeout,
            async lines =>
            {
                try
                {
                    await InitializeAsync(lines).ConfigureAwait(false);
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

                    var now = timeProvider.GetLocalNow();
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
        if (response.TryGetProperty("rateLimitsByLimitId", out var byId)
            && byId.ValueKind == JsonValueKind.Object
            && byId.TryGetProperty("codex", out var codex))
        {
            return codex;
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

}
