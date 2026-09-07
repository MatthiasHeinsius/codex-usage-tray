using System.Globalization;
using System.Text.Json;

namespace CodexUsageTray;

internal static class SelfTest
{
    public static int Run()
    {
        const string limitsJson = """
            {
              "rateLimits": {
                "limitName": "Codex",
                "planType": "plus",
                "primary": { "usedPercent": 24, "windowDurationMins": 300, "resetsAt": 1788780000 },
                "secondary": { "usedPercent": 61, "windowDurationMins": 10080, "resetsAt": 1789200000 }
              },
              "rateLimitsByLimitId": null
            }
            """;
        const string usageJson = """
            {
              "summary": { "lifetimeTokens": 123456789 },
              "dailyUsageBuckets": [
                { "startDate": "2026-09-06", "tokens": 10 },
                { "startDate": "2026-09-07", "tokens": 987654 }
              ]
            }
            """;

        using var limits = JsonDocument.Parse(limitsJson);
        using var usage = JsonDocument.Parse(usageJson);
        var snapshot = CodexAppServerClient.ParseSnapshot(
            limits.RootElement,
            usage.RootElement,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2)));

        Assert(snapshot.FiveHour?.RemainingPercent == 76, "5-hour window");
        Assert(snapshot.Weekly?.RemainingPercent == 39, "weekly window");
        Assert(snapshot.TodayTokens == 987654, "daily tokens");
        Assert(snapshot.LifetimeTokens == 123456789, "lifetime tokens");
        Assert(snapshot.Plan == "plus", "plan");

        using var lengthAhead = new LengthAheadStream(length: 10, position: 3);
        using var copiedBytes = new MemoryStream();
        Assert(LocalTokenUsageReader.CopyUnreadBytes(lengthAhead, copiedBytes) == 3,
            "local token reader advances by consumed bytes");

        Assert(UsageText.TokensForCulture(snapshot.LifetimeTokens, CultureInfo.GetCultureInfo("en-US")) == "123.46M",
            "English token formatting");
        Assert(UsageText.TokensForCulture(snapshot.LifetimeTokens, CultureInfo.GetCultureInfo("de-DE")) == "123,46M",
            "German token formatting");
        Assert(
            UsageText.CompactCountdown(snapshot.FiveHour, new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2)))
                == "1h 20m",
            "compact countdown");

        const string multiBucketJson = """
            {
              "rateLimits": { "primary": { "usedPercent": 99, "windowDurationMins": 300 } },
              "rateLimitsByLimitId": {
                "other": { "primary": { "usedPercent": 80, "windowDurationMins": 60 } },
                "codex": {
                  "primary": { "usedPercent": 25, "windowDurationMins": 10080 },
                  "secondary": { "usedPercent": 10, "windowDurationMins": 300 }
                }
              }
            }
            """;
        using var multiBucket = JsonDocument.Parse(multiBucketJson);
        var selected = CodexAppServerClient.ParseSnapshot(multiBucket.RootElement, usage.RootElement,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2)));
        Assert(selected.FiveHour?.RemainingPercent == 90, "multi-bucket 5-hour window");
        Assert(selected.Weekly?.RemainingPercent == 75, "multi-bucket weekly window");

        using var icon = TrayIconRenderer.Create(65, 7);
        Assert(icon.Width == 32 && icon.Height == 32, "dual-ring tray icon");

        using var popup = new UsagePopupForm();
        Assert(!popup.HeaderControlsOverlap, "usage link does not overlap title");
        Assert(popup.InferenceDividerPaddingIsBalanced, "inference divider padding is balanced");
        popup.Location = new Point(-10_000, -10_000);
        popup.Show();
        try
        {
            popup.SetViewModeForScreenshot(compact: true);
            Assert(popup.CompactRefreshLayoutIsCorrect, "compact refresh button fits without extra height");
        }
        finally
        {
            popup.Hide();
        }

        var expiredWindow = new UsageWindow(100, 300, DateTimeOffset.FromUnixTimeSeconds(100));
        Assert(WindowStartSettings.IsExpiredAndUnstarted(expiredWindow, DateTimeOffset.FromUnixTimeSeconds(101), null),
            "expired window needs start");
        Assert(!WindowStartSettings.IsExpiredAndUnstarted(expiredWindow, DateTimeOffset.FromUnixTimeSeconds(101), 100),
            "completed window start is not repeated");
        Assert(!WindowStartSettings.IsExpiredAndUnstarted(expiredWindow, DateTimeOffset.FromUnixTimeSeconds(99), null),
            "future window is not started");

        var localUsageLines = new[]
        {
            "{\"timestamp\":\"2026-09-07T08:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":1200}}}",
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":800}}}",
            "{\"timestamp\":\"2026-09-06T09:00:00Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":9999}}}",
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{}}"
        };
        var localUsage = LocalTokenUsageReader.SumLinesForDate(localUsageLines, new DateOnly(2026, 9, 7));
        Assert(localUsage.Found && localUsage.Tokens == 2000, "local daily inference fallback");

        const string delayedUsageJson = """
            {
              "summary": { "lifetimeTokens": 10000 },
              "dailyUsageBuckets": [
                { "startDate": "2026-09-05", "tokens": 400 },
                { "startDate": "2026-09-06", "tokens": 600 }
              ]
            }
            """;
        using var delayedUsage = JsonDocument.Parse(delayedUsageJson);
        var delayedSnapshot = CodexAppServerClient.ParseSnapshot(
            limits.RootElement,
            delayedUsage.RootElement,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2)));
        var combinedSnapshot = CodexAppServerClient.ApplyLocalTodayFallback(
            delayedSnapshot,
            delayedUsage.RootElement,
            2000,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2)));
        Assert(combinedSnapshot.TodayTokens == 2000 && combinedSnapshot.TodayTokensAreLocal,
            "local daily inference is displayed");
        Assert(combinedSnapshot.LifetimeTokens == 12000 && combinedSnapshot.LifetimeIncludesLocalToday,
            "yesterday lifetime includes local today");

        var currentSnapshot = CodexAppServerClient.ApplyLocalTodayFallback(
            snapshot,
            usage.RootElement,
            2000,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2)));
        Assert(currentSnapshot.LifetimeTokens == 123456789 && !currentSnapshot.LifetimeIncludesLocalToday,
            "current lifetime is not double counted");

        Console.WriteLine("All self-tests passed.");
        return 0;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {name}");
        }
    }

    private sealed class LengthAheadStream(long length, long position) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; } = position;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
