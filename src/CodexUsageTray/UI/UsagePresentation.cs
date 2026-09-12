using System.Collections.Immutable;
using System.Globalization;

namespace CodexUsageTray;

internal abstract record UsagePresentation(
    UsagePresentation.PopupPresentation Popup,
    UsagePresentation.TrayPresentation Tray,
    ImmutableArray<UsagePresentation.NoticePresentation> Notices)
{
    public static Initial CreateInitial()
    {
        var unavailable = UnavailableAllowance();
        return new Initial(
            new PopupPresentation(
                "Connecting...",
                unavailable,
                unavailable,
                "Unavailable",
                "Unavailable",
                "Not updated yet"),
            new TrayPresentation(100, 100, "Codex usage · connecting"));
    }

    public static Ready Create(
        UsageSnapshot snapshot,
        DateTimeOffset now,
        IFormatProvider formatProvider) =>
        Create(
            snapshot,
            AllowanceWindowActivationResult.Empty,
            notificationsEnabled: false,
            now,
            formatProvider);

    internal static Ready Create(
        UsageSnapshot snapshot,
        AllowanceWindowActivationResult allowanceEvents,
        bool notificationsEnabled,
        DateTimeOffset now,
        IFormatProvider formatProvider)
    {
        var fiveHour = PresentAllowance(snapshot.FiveHour, now, formatProvider);
        var weekly = PresentAllowance(snapshot.Weekly, now, formatProvider);
        var observationText = PresentLatestObservationTime(snapshot, formatProvider);
        var popup = new PopupPresentation(
            AccountStatus(snapshot),
            fiveHour,
            weekly,
            TokenLabel(snapshot.TodayTokens, formatProvider),
            TokenLabel(snapshot.LifetimeTokens, formatProvider),
            observationText);
        var tray = new TrayPresentation(
            snapshot.FiveHour?.RemainingPercent ?? 100,
            snapshot.Weekly?.RemainingPercent ?? 100,
            $"Codex · 5h {PercentOrUnknown(snapshot.FiveHour)}% · week {PercentOrUnknown(snapshot.Weekly)}%");

        return new Ready(
            popup,
            tray,
            PresentNotices(allowanceEvents, notificationsEnabled));
    }

    internal static Loading CreateLoading(
        Ready? previous,
        string? failureMessage = null,
        ImmutableArray<NoticePresentation>? notices = null)
    {
        var initial = previous is null ? CreateInitial() : null;
        var popup = previous?.Popup ?? initial!.Popup;
        popup = popup with
        {
            AccountStatus = "Reading your Codex account",
            UpdatedText = failureMessage is null
                ? previous is null ? "Not updated yet" : $"{previous.Popup.UpdatedText} · Updating"
                : previous is null ? failureMessage : $"Stale · {failureMessage}"
        };

        var tray = previous?.Tray ?? initial!.Tray;
        tray = tray with
        {
            Tooltip = failureMessage is null
                ? previous is null ? tray.Tooltip : $"{tray.Tooltip} · updating"
                : previous is null
                    ? $"Codex usage · unavailable · updating · {failureMessage}"
                    : $"{tray.Tooltip} · stale · updating · {failureMessage}"
        };

        return new Loading(
            popup,
            tray,
            notices ?? ImmutableArray<NoticePresentation>.Empty,
            failureMessage);
    }

    internal static Failed CreateFailed(Ready? previous, string failureMessage)
    {
        var initial = previous is null ? CreateInitial() : null;
        var popup = (previous?.Popup ?? initial!.Popup) with
        {
            AccountStatus = "Could not refresh",
            UpdatedText = previous is null ? failureMessage : $"Stale · {failureMessage}"
        };
        var priorTray = previous?.Tray ?? initial!.Tray;
        var tray = priorTray with
        {
            Tooltip = previous is null
                ? $"Codex usage · unavailable · {failureMessage}"
                : $"{priorTray.Tooltip} · stale · {failureMessage}"
        };
        return new Failed(popup, tray, failureMessage);
    }

    private static AllowancePresentation UnavailableAllowance() => new(
        ProgressValue: 0,
        RemainingText: "Unavailable",
        ResetText: "Reset time unavailable",
        CompactResetText: "Reset unknown");

    private static string PresentLatestObservationTime(
        UsageSnapshot snapshot,
        IFormatProvider formatProvider)
    {
        var latest = snapshot.ActivityObservedAt is { } activityObservedAt
            && activityObservedAt > snapshot.AllowanceObservedAt
                ? activityObservedAt
                : snapshot.AllowanceObservedAt;
        return $"Updated {latest.LocalDateTime.ToString("t", formatProvider)}";
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

    private static string PercentOrUnknown(AllowanceWindow? window) =>
        window?.RemainingPercent.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string FormatNumber(int value, IFormatProvider formatProvider) =>
        string.Format(formatProvider, "{0}", value);

    private static ImmutableArray<NoticePresentation> PresentNotices(
        AllowanceWindowActivationResult events,
        bool notificationsEnabled)
    {
        var notices = new List<NoticePresentation>();
        if (notificationsEnabled)
        {
            if (events.UsedUp != AllowanceWindows.None)
            {
                notices.Add(new NoticePresentation(
                    $"{AllowanceNames(events.UsedUp)} allowance used up.",
                    NoticeSeverity.Warning,
                    TimeSpan.FromSeconds(5)));
            }

            if (events.Reset != AllowanceWindows.None)
            {
                notices.Add(new NoticePresentation(
                    $"{AllowanceNames(events.Reset)} allowance reset.",
                    NoticeSeverity.Information,
                    TimeSpan.FromSeconds(5)));
            }
        }

        return [.. notices];
    }

    private static string AllowanceNames(AllowanceWindows windows) => windows switch
    {
        AllowanceWindows.FiveHour => "5-hour",
        AllowanceWindows.Weekly => "Weekly",
        AllowanceWindows.FiveHour | AllowanceWindows.Weekly => "5-hour and weekly",
        _ => "Codex"
    };

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

    internal sealed record NoticePresentation(
        string Message,
        NoticeSeverity Severity,
        TimeSpan Duration);

    internal sealed record Initial(
        PopupPresentation Popup,
        TrayPresentation Tray)
        : UsagePresentation(Popup, Tray, ImmutableArray<NoticePresentation>.Empty);

    internal sealed record Loading(
        PopupPresentation Popup,
        TrayPresentation Tray,
        ImmutableArray<NoticePresentation> Notices,
        string? FailureMessage)
        : UsagePresentation(Popup, Tray, Notices);

    internal sealed record Ready(
        PopupPresentation Popup,
        TrayPresentation Tray,
        ImmutableArray<NoticePresentation> Notices)
        : UsagePresentation(Popup, Tray, Notices)
    {
        public Ready WithoutNotices() =>
            Notices.Length == 0 ? this : new Ready(Popup, Tray, ImmutableArray<NoticePresentation>.Empty);
    }

    internal sealed record Failed(
        PopupPresentation Popup,
        TrayPresentation Tray,
        string FailureMessage)
        : UsagePresentation(Popup, Tray, ImmutableArray<NoticePresentation>.Empty);

    internal enum NoticeSeverity
    {
        Information,
        Warning
    }
}
