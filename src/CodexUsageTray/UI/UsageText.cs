using System.Globalization;

namespace CodexUsageTray;

internal static class UsageText
{
    public static string PercentLeft(UsageWindow? window) =>
        window is null ? "Unavailable" : $"{window.RemainingPercent}% left";

    public static string ResetText(UsageWindow? window, DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } reset)
        {
            return "Reset time unavailable";
        }

        var local = reset.ToLocalTime();
        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
        {
            return $"Reset due {local:t}";
        }

        var countdown = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";

        return $"Resets in {countdown} · {local:g}";
    }

    public static string CompactCountdown(UsageWindow? window, DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } reset)
        {
            return "Reset unknown";
        }

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset due";
        }

        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";
    }

    public static string Tokens(long? tokens) => tokens switch
    {
        null => "Unavailable",
        < 1_000 => tokens.Value.ToString("N0", CultureInfo.CurrentCulture),
        < 1_000_000 => $"{tokens.Value / 1_000d:0.#}K",
        < 1_000_000_000 => $"{tokens.Value / 1_000_000d:0.##}M",
        _ => $"{tokens.Value / 1_000_000_000d:0.##}B"
    };

    public static string TokenLabel(long? tokens) => tokens is null ? "Unavailable" : $"{Tokens(tokens)} tokens";

    public static string FormatConsole(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        return string.Join(Environment.NewLine,
            $"5-hour: {PercentLeft(snapshot.FiveHour)} ({ResetText(snapshot.FiveHour, now)})",
            $"Weekly: {PercentLeft(snapshot.Weekly)} ({ResetText(snapshot.Weekly, now)})",
            $"Inference today{(snapshot.TodayTokensAreLocal ? " on this PC" : string.Empty)}: {(snapshot.TodayTokens is { } today ? $"{today.ToString("N0", CultureInfo.InvariantCulture)} tokens" : "unavailable")}",
            $"Inference lifetime{(snapshot.LifetimeIncludesLocalToday ? " including this PC today" : string.Empty)}: {(snapshot.LifetimeTokens is { } lifetime ? $"{lifetime.ToString("N0", CultureInfo.InvariantCulture)} tokens" : "unavailable")}",
            $"Plan: {snapshot.Plan ?? "unknown"}",
            $"Updated: {snapshot.RetrievedAt.LocalDateTime:G}");
    }
}
