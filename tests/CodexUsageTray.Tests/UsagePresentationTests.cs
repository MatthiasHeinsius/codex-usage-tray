using System.Globalization;

namespace CodexUsageTray.Tests;

public sealed class UsagePresentationTests
{
    [Fact]
    public void CreateDerivesPopupTrayAndConsolePresentation()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var fiveHourReset = now.AddHours(3).AddMinutes(12);
        var weeklyReset = now.AddDays(3).AddHours(8);
        var snapshot = CreateSnapshot(
            now,
            new AllowanceWindow(36, TimeSpan.FromHours(5), fiveHourReset),
            new AllowanceWindow(58, TimeSpan.FromDays(7), weeklyReset),
            lifetimeTokens: 123_456_789,
            todayTokens: 784_200,
            plan: "plus",
            limitName: "Codex");
        var culture = CultureInfo.GetCultureInfo("en-US");

        var presentation = UsagePresentation.Create(snapshot, now, culture);

        Assert.Equal("Plus · Codex", presentation.Popup.AccountStatus);
        Assert.Equal(64, presentation.Popup.FiveHour.ProgressValue);
        Assert.Equal("64% left", presentation.Popup.FiveHour.RemainingText);
        Assert.Equal("3h 12m", presentation.Popup.FiveHour.CompactResetText);
        Assert.Equal(
            $"Resets in 3h 12m · {fiveHourReset.ToLocalTime().ToString("g", culture)}",
            presentation.Popup.FiveHour.ResetText);
        Assert.Equal(42, presentation.Popup.Weekly.ProgressValue);
        Assert.Equal("3d 8h", presentation.Popup.Weekly.CompactResetText);
        Assert.Equal("784.2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal("123.46M tokens", presentation.Popup.LifetimeTokens);
        Assert.Equal($"Updated {now.LocalDateTime.ToString("t", culture)}", presentation.Popup.UpdatedText);
        Assert.Equal(64, presentation.Tray.FiveHourRemaining);
        Assert.Equal(42, presentation.Tray.WeeklyRemaining);
        Assert.Equal("Codex · 5h 64% · week 42%", presentation.Tray.Tooltip);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                $"5-hour: 64% left (Resets in 3h 12m · {fiveHourReset.ToLocalTime().ToString("g", culture)})",
                $"Weekly: 42% left (Resets in 3d 8h · {weeklyReset.ToLocalTime().ToString("g", culture)})",
                "Inference today: 784,200 tokens",
                "Inference lifetime: 123,456,789 tokens",
                "Plan: plus",
                $"Updated: {now.LocalDateTime.ToString("G", culture)}"),
            presentation.ConsoleText);
    }

    [Fact]
    public void CreateHandlesUnavailableValuesAndAnEmptyPlan()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = CreateSnapshot(
            now,
            fiveHour: null,
            weekly: null,
            lifetimeTokens: null,
            todayTokens: null,
            plan: string.Empty,
            limitName: "Codex");

        var presentation = UsagePresentation.Create(
            snapshot,
            now,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("Codex", presentation.Popup.AccountStatus);
        Assert.Equal(0, presentation.Popup.FiveHour.ProgressValue);
        Assert.Equal("Unavailable", presentation.Popup.FiveHour.RemainingText);
        Assert.Equal("Reset time unavailable", presentation.Popup.FiveHour.ResetText);
        Assert.Equal("Reset unknown", presentation.Popup.FiveHour.CompactResetText);
        Assert.Equal("Unavailable", presentation.Popup.TodayTokens);
        Assert.Equal(100, presentation.Tray.FiveHourRemaining);
        Assert.Equal(100, presentation.Tray.WeeklyRemaining);
        Assert.Equal("Codex · 5h ?% · week ?%", presentation.Tray.Tooltip);
        Assert.Contains("Inference today: unavailable", presentation.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFormatsTokensWithTheRequestedCulture()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = CreateSnapshot(
            now,
            fiveHour: null,
            weekly: null,
            lifetimeTokens: 123_456_789,
            todayTokens: 1_234,
            plan: null,
            limitName: null);

        var presentation = UsagePresentation.Create(
            snapshot,
            now,
            CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("Signed in through Codex", presentation.Popup.AccountStatus);
        Assert.Equal("1,2K tokens", presentation.Popup.TodayTokens);
        Assert.Equal("123,46M tokens", presentation.Popup.LifetimeTokens);
    }

    [Fact]
    public void CreateUsesTheRequestedFormatProviderForAllowanceNumbers()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = CreateSnapshot(
            now,
            new AllowanceWindow(36, TimeSpan.FromHours(5), now.AddHours(3).AddMinutes(12)),
            weekly: null,
            lifetimeTokens: null,
            todayTokens: null,
            plan: "plus",
            limitName: "Codex");

        var presentation = UsagePresentation.Create(snapshot, now, new MarkedIntegerFormatProvider());

        Assert.Equal("[64]% left", presentation.Popup.FiveHour.RemainingText);
        Assert.Equal("[3]h [12]m", presentation.Popup.FiveHour.CompactResetText);
    }

    [Fact]
    public void CreateIncludesLocalActivityProvenanceInConsolePresentation()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = UsageSnapshot.Reconcile(
            previous: null,
            new AccountUsageObservation(
                now,
                [],
                "plus",
                "Codex",
                new AccountActivityObservation.Observed(
                    LifetimeTokens: 10_000,
                    TodayTokens: null,
                    LatestDailyBucketDate: new DateOnly(2026, 9, 6))),
            new LocalUsageObservation(new DateOnly(2026, 9, 7), 2_000));

        var presentation = UsagePresentation.Create(
            snapshot,
            now,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Contains("Inference today on this PC: 2,000 tokens", presentation.ConsoleText, StringComparison.Ordinal);
        Assert.Contains(
            "Inference lifetime including this PC today: 12,000 tokens",
            presentation.ConsoleText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDistinguishesDueAndUnknownResetTimes()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = CreateSnapshot(
            now,
            new AllowanceWindow(10, TimeSpan.FromHours(5), now),
            new AllowanceWindow(20, TimeSpan.FromDays(7), ResetsAt: null),
            lifetimeTokens: null,
            todayTokens: null,
            plan: "plus",
            limitName: "Codex");
        var culture = CultureInfo.GetCultureInfo("en-US");

        var presentation = UsagePresentation.Create(snapshot, now, culture);

        Assert.Equal($"Reset due {now.ToLocalTime().ToString("t", culture)}", presentation.Popup.FiveHour.ResetText);
        Assert.Equal("Reset due", presentation.Popup.FiveHour.CompactResetText);
        Assert.Equal("Reset time unavailable", presentation.Popup.Weekly.ResetText);
        Assert.Equal("Reset unknown", presentation.Popup.Weekly.CompactResetText);
    }

    [Theory]
    [InlineData(999L, "999 tokens")]
    [InlineData(1_234L, "1.2K tokens")]
    [InlineData(1_234_567L, "1.23M tokens")]
    [InlineData(1_234_567_890L, "1.23B tokens")]
    public void CreateFormatsEachCompactTokenMagnitude(long tokens, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));
        var snapshot = CreateSnapshot(
            now,
            fiveHour: null,
            weekly: null,
            lifetimeTokens: tokens,
            todayTokens: null,
            plan: "plus",
            limitName: "Codex");

        var presentation = UsagePresentation.Create(
            snapshot,
            now,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal(expected, presentation.Popup.LifetimeTokens);
    }

    private static UsageSnapshot CreateSnapshot(
        DateTimeOffset observedAt,
        AllowanceWindow? fiveHour,
        AllowanceWindow? weekly,
        long? lifetimeTokens,
        long? todayTokens,
        string? plan,
        string? limitName)
    {
        var windows = new[] { fiveHour, weekly }.OfType<AllowanceWindow>().ToArray();
        return UsageSnapshot.Reconcile(
            previous: null,
            new AccountUsageObservation(
                observedAt,
                windows,
                plan,
                limitName,
                new AccountActivityObservation.Observed(
                    lifetimeTokens,
                    todayTokens,
                    LatestDailyBucketDate: DateOnly.FromDateTime(observedAt.LocalDateTime))),
            local: null);
    }

    private sealed class MarkedIntegerFormatProvider : IFormatProvider, ICustomFormatter
    {
        public object? GetFormat(Type? formatType) =>
            formatType == typeof(ICustomFormatter) ? this : null;

        public string Format(string? format, object? argument, IFormatProvider? formatProvider) =>
            argument is int value
                ? $"[{value.ToString(CultureInfo.InvariantCulture)}]"
                : argument is IFormattable formattable
                    ? formattable.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty
                    : argument?.ToString() ?? string.Empty;
    }
}
