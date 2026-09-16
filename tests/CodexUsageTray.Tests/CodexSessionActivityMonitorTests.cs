using System.Text;

namespace CodexUsageTray.Tests;

public sealed class CodexSessionActivityMonitorTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    [Fact]
    public void ReadsRecentTokenActivityAndModelFromAnExistingSessionTail()
    {
        using var directory = new TemporaryDirectory("session-activity-existing");
        var session = SessionPath(directory);
        var tokenAt = DateTimeOffset.Now.AddSeconds(-2);
        File.WriteAllText(
            session,
            ModelLine(tokenAt.AddSeconds(-1), "gpt-6-astra") + "\n" + TokenLine(tokenAt) + "\n",
            Utf8);

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        Assert.Equal(CodexModel.Astra, monitor.Current.Model);
        Assert.Equal(tokenAt, monitor.Current.LastTokenAt);
    }

    [Fact]
    public async Task WatchesSessionAppendsWithoutPollingTheCodexCli()
    {
        using var directory = new TemporaryDirectory("session-activity-watch");
        var session = SessionPath(directory);
        File.WriteAllText(session, string.Empty, Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        var observed = new TaskCompletionSource<CodexSessionActivity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) =>
        {
            var current = monitor.Current;
            if (current is { Model: CodexModel.Luna, LastTokenAt: not null })
            {
                observed.TrySetResult(current);
            }
        };
        var tokenAt = DateTimeOffset.Now;

        File.AppendAllText(
            session,
            ModelLine(tokenAt.AddMilliseconds(-10), "gpt-5.6-luna") + "\n" +
            EventTokenLine(tokenAt) + "\n",
            Utf8);

        var activity = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(CodexModel.Luna, activity.Model);
        Assert.Equal(tokenAt, activity.LastTokenAt);
    }

    [Fact]
    public async Task UsesTheModelFromTheSessionThatProducedTheLatestTokens()
    {
        using var directory = new TemporaryDirectory("session-activity-concurrent");
        var now = DateTimeOffset.Now;
        var firstSession = SessionPath(directory, "first.jsonl");
        var secondSession = SessionPath(directory, "second.jsonl");
        File.WriteAllText(
            firstSession,
            ModelLine(now.AddSeconds(-4), "gpt-6-astra") + "\n" + TokenLine(now.AddSeconds(-3)) + "\n",
            Utf8);
        File.WriteAllText(
            secondSession,
            ModelLine(now.AddSeconds(-1), "gpt-5.6-luna") + "\n",
            Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        Assert.Equal(CodexModel.Astra, monitor.Current.Model);

        var observed = new TaskCompletionSource<CodexSessionActivity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) =>
        {
            if (monitor.Current is { Model: CodexModel.Luna } activity)
            {
                observed.TrySetResult(activity);
            }
        };
        File.AppendAllText(secondSession, TokenLine(now) + "\n", Utf8);

        var latest = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(now, latest.LastTokenAt);
    }

    [Fact]
    public void ReplacesAnOlderKnownModelWithAnUnrecognizedCurrentModel()
    {
        using var directory = new TemporaryDirectory("session-activity-unknown-model");
        var session = SessionPath(directory);
        var now = DateTimeOffset.Now;
        File.WriteAllText(
            session,
            ModelLine(now.AddSeconds(-3), "gpt-6-astra") + "\n" +
            ModelLine(now.AddSeconds(-2), "gpt-7-orion") + "\n" +
            TokenLine(now.AddSeconds(-1)) + "\n",
            Utf8);

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        Assert.Equal(CodexModel.Unknown, monitor.Current.Model);
    }

    private static string SessionPath(TemporaryDirectory directory, string fileName = "session.jsonl")
    {
        var path = Path.Combine(directory.RootPath, "sessions", "2026", "09", "16", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string ModelLine(DateTimeOffset timestamp, string model) =>
        $"{{\"timestamp\":\"{timestamp:O}\",\"type\":\"turn_context\",\"payload\":{{\"model\":\"{model}\"}}}}";

    private static string TokenLine(DateTimeOffset timestamp) =>
        $"{{\"timestamp\":\"{timestamp:O}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":12}}}}}}";

    private static string EventTokenLine(DateTimeOffset timestamp) =>
        $"{{\"timestamp\":\"{timestamp:O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\"}}}}";
}
