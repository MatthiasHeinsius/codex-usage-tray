using System.Globalization;

namespace CodexUsageTray;

internal sealed record UsagePresentation
{
    private UsagePresentation(
        PopupPresentation popup,
        TrayPresentation tray,
        string consoleText)
    {
        Popup = popup;
        Tray = tray;
        ConsoleText = consoleText;
    }

    public PopupPresentation Popup { get; }
    public TrayPresentation Tray { get; }
    public string ConsoleText { get; }

    public static UsagePresentation Create(
        UsageSnapshot snapshot,
        DateTimeOffset now,
        IFormatProvider formatProvider)
    {
        var fiveHour = PresentAllowance(snapshot.FiveHour, now, formatProvider);
        var weekly = PresentAllowance(snapshot.Weekly, now, formatProvider);
        var popup = new PopupPresentation(
            AccountStatus(snapshot),
            fiveHour,
            weekly,
            TokenLabel(snapshot.TodayTokens, formatProvider),
            TokenLabel(snapshot.LifetimeTokens, formatProvider),
            $"Updated {snapshot.RetrievedAt.LocalDateTime.ToString("t", formatProvider)}");
        var tray = new TrayPresentation(
            snapshot.FiveHour?.RemainingPercent ?? 100,
            snapshot.Weekly?.RemainingPercent ?? 100,
            $"Codex · 5h {PercentOrUnknown(snapshot.FiveHour)}% · week {PercentOrUnknown(snapshot.Weekly)}%");
        var consoleText = string.Join(
            Environment.NewLine,
            $"5-hour: {fiveHour.RemainingText} ({fiveHour.ResetText})",
            $"Weekly: {weekly.RemainingText} ({weekly.ResetText})",
            $"Inference today{(snapshot.TodayTokensAreLocal ? " on this PC" : string.Empty)}: {ConsoleTokens(snapshot.TodayTokens)}",
            $"Inference lifetime{(snapshot.LifetimeIncludesLocalToday ? " including this PC today" : string.Empty)}: {ConsoleTokens(snapshot.LifetimeTokens)}",
            $"Plan: {snapshot.Plan ?? "unknown"}",
            $"Updated: {snapshot.RetrievedAt.LocalDateTime.ToString("G", formatProvider)}");

        return new UsagePresentation(popup, tray, consoleText);
    }

    private static AllowancePresentation PresentAllowance(
        AllowanceWindow? window,
        DateTimeOffset now,
        IFormatProvider formatProvider)
    {
        if (window is null)
        {
            return new AllowancePresentation(
                ProgressValue: 0,
                RemainingText: "Unavailable",
                ResetText: "Reset time unavailable",
                CompactResetText: "Reset unknown");
        }

        return new AllowancePresentation(
            window.RemainingPercent,
            $"{FormatNumber(window.RemainingPercent, formatProvider)}% left",
            ResetText(window.ResetsAt, now, formatProvider),
            CompactResetText(window.ResetsAt, now, formatProvider));
    }

    private static string ResetText(
        DateTimeOffset? reset,
        DateTimeOffset now,
        IFormatProvider formatProvider)
    {
        if (reset is not { } resetAt)
        {
            return "Reset time unavailable";
        }

        var local = resetAt.ToLocalTime();
        var remaining = resetAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return $"Reset due {local.ToString("t", formatProvider)}";
        }

        return $"Resets in {Countdown(remaining, formatProvider)} · {local.ToString("g", formatProvider)}";
    }

    private static string CompactResetText(
        DateTimeOffset? reset,
        DateTimeOffset now,
        IFormatProvider formatProvider)
    {
        if (reset is not { } resetAt)
        {
            return "Reset unknown";
        }

        var remaining = resetAt - now;
        return remaining <= TimeSpan.Zero ? "Reset due" : Countdown(remaining, formatProvider);
    }

    private static string Countdown(TimeSpan remaining, IFormatProvider formatProvider) =>
        remaining.TotalDays >= 1
            ? $"{FormatNumber((int)remaining.TotalDays, formatProvider)}d {FormatNumber(remaining.Hours, formatProvider)}h"
            : remaining.TotalHours >= 1
                ? $"{FormatNumber((int)remaining.TotalHours, formatProvider)}h {FormatNumber(remaining.Minutes, formatProvider)}m"
                : $"{FormatNumber(Math.Max(1, remaining.Minutes), formatProvider)}m";

    private static string AccountStatus(UsageSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Plan) && string.IsNullOrWhiteSpace(snapshot.LimitName))
        {
            return "Signed in through Codex";
        }

        var plan = string.IsNullOrWhiteSpace(snapshot.Plan)
            ? null
            : char.ToUpperInvariant(snapshot.Plan[0]) + snapshot.Plan[1..];
        return string.Join(" · ", new[] { plan, snapshot.LimitName }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string TokenLabel(long? tokens, IFormatProvider formatProvider) =>
        tokens is null ? "Unavailable" : $"{CompactTokens(tokens.Value, formatProvider)} tokens";

    private static string CompactTokens(long tokens, IFormatProvider formatProvider) =>
        tokens switch
        {
            < 1_000 => tokens.ToString("N0", formatProvider),
            < 1_000_000 => $"{(tokens / 1_000d).ToString("0.#", formatProvider)}K",
            < 1_000_000_000 => $"{(tokens / 1_000_000d).ToString("0.##", formatProvider)}M",
            _ => $"{(tokens / 1_000_000_000d).ToString("0.##", formatProvider)}B"
        };

    private static string ConsoleTokens(long? tokens) =>
        tokens is { } value
            ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} tokens"
            : "unavailable";

    private static string PercentOrUnknown(AllowanceWindow? window) =>
        window?.RemainingPercent.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string FormatNumber(int value, IFormatProvider formatProvider) =>
        string.Format(formatProvider, "{0}", value);

    internal sealed record PopupPresentation(
        string AccountStatus,
        AllowancePresentation FiveHour,
        AllowancePresentation Weekly,
        string TodayTokens,
        string LifetimeTokens,
        string UpdatedText);

    internal sealed record AllowancePresentation(
        int ProgressValue,
        string RemainingText,
        string ResetText,
        string CompactResetText);

    internal sealed record TrayPresentation(
        int FiveHourRemaining,
        int WeeklyRemaining,
        string Tooltip);
}
