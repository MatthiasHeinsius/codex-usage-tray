using System.Text;

namespace CodexUsageTray.Tests;

public sealed class CodexSessionActivityMonitorTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    [Fact]
    public async Task LockedSessionPreservesItsLastActivityAndResumesOnTheNextWrite()
    {
        using var directory = new TemporaryDirectory("session-activity-locked");
        var session = SessionPath(directory);
        var sentinel = SessionPath(directory, "sentinel.jsonl");
        var now = DateTimeOffset.Now;
        var original = new CodexSessionActivity(now.AddSeconds(-2), CodexModel.Astra, "6");
        File.WriteAllText(session, ModelLine(now.AddSeconds(-3), "gpt-6-astra") + "\n" +
            TokenLine(original.LastTokenAt!.Value) + "\n", Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        await ObserveChangeAsync(monitor, expected: original);

        using (var locked = new FileStream(session, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            locked.Write(Utf8.GetBytes(ModelLine(now, "gpt-6-luna") + "\n" + TokenLine(now) + "\n"));
            locked.Flush(flushToDisk: true);
            // An observable second session lets the watcher process writes while the first is locked.
            await ObserveChangeAsync(monitor,
                () => File.WriteAllText(sentinel, TokenLine(now.AddSeconds(-1)) + "\n", Utf8),
                new CodexSessionActivity(now.AddSeconds(-1), CodexModel.Unknown));
            await ObserveChangeAsync(monitor, () => File.Delete(sentinel), original);
        }

        await ObserveChangeAsync(monitor, () => File.AppendAllText(session, "\n", Utf8),
            new CodexSessionActivity(now, CodexModel.Luna, "6"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsRecentTokenActivityAndModelFromAnExistingSession(bool largeSession)
    {
        using var directory = new TemporaryDirectory("session-activity-existing");
        var session = SessionPath(directory);
        var tokenAt = DateTimeOffset.Now.AddSeconds(-2);
        File.WriteAllText(
            session,
            ModelLine(tokenAt.AddSeconds(-1), "gpt-6-astra") + "\n" +
            (largeSession ? PaddingLine : string.Empty) + TokenLine(tokenAt) + "\n",
            Utf8);

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(tokenAt, CodexModel.Astra, "6"));

        Assert.Equal(CodexModel.Astra, monitor.Current.Model);
        Assert.Equal("6", monitor.Current.ModelVersion);
        Assert.Equal(tokenAt, monitor.Current.LastTokenAt);
    }

    [Theory]
    [InlineData("GPT-Sol-6", "Sol")]
    [InlineData("GPT-Luna-6", "Luna")]
    public async Task ReadsVersionWhenTheModelFamilyPrecedesIt(string modelName, string expectedModel)
    {
        using var directory = new TemporaryDirectory("session-activity-reversed-model");
        var session = SessionPath(directory);
        var tokenAt = DateTimeOffset.Now.AddSeconds(-2);
        File.WriteAllText(
            session,
            ModelLine(tokenAt.AddSeconds(-1), modelName) + "\n" + TokenLine(tokenAt) + "\n",
            Utf8);

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        await ObserveChangeAsync(monitor,
            expected: new CodexSessionActivity(tokenAt, Enum.Parse<CodexModel>(expectedModel), "6"));
        Assert.Equal(expectedModel, monitor.Current.Model.ToString());
        Assert.Equal("6", monitor.Current.ModelVersion);
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
        Assert.Equal("5.6", activity.ModelVersion);
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
        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now.AddSeconds(-3), CodexModel.Astra, "6"));
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
        Assert.Equal("5.6", latest.ModelVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoresAutoReviewSessions(bool largeSession)
    {
        using var directory = new TemporaryDirectory("session-activity-auto-review");
        var now = DateTimeOffset.Now;
        var userSession = SessionPath(directory, "user.jsonl");
        var reviewSession = SessionPath(directory, "review.jsonl");
        var userTokenAt = now.AddSeconds(-3);
        File.WriteAllText(
            userSession,
            ModelLine(now.AddSeconds(-4), "gpt-6-astra") + "\n" + TokenLine(userTokenAt) + "\n",
            Utf8);
        File.WriteAllText(
            reviewSession,
            ModelLine(now.AddSeconds(-2), "codex-auto-review") + "\n" +
            (largeSession ? PaddingLine : string.Empty) + TokenLine(now.AddSeconds(-1)) + "\n",
            Utf8);

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(userTokenAt, CodexModel.Astra, "6"));
        Assert.Equal(CodexModel.Astra, monitor.Current.Model);
        Assert.Equal(userTokenAt, monitor.Current.LastTokenAt);
    }

    [Theory]
    [InlineData("codex-auto-review")]
    [InlineData("gpt-6-luna")]
    public async Task FindsStartupActivityBeyondTwelveIneligibleSessions(string newerModel)
    {
        using var directory = new TemporaryDirectory("session-activity-startup-selection");
        var now = DateTimeOffset.Now;
        var userSession = SessionPath(directory, "user.jsonl");
        var userTokenAt = now.AddMinutes(-1);
        File.WriteAllText(userSession,
            ModelLine(userTokenAt, "gpt-6-astra") + "\n" + TokenLine(userTokenAt) + "\n", Utf8);
        File.SetLastWriteTimeUtc(userSession, userTokenAt.UtcDateTime);
        for (var index = 0; index < 12; index++)
        {
            var path = SessionPath(directory, $"newer-{index}.jsonl");
            File.WriteAllText(path, ModelLine(now, newerModel) + "\n" +
                (newerModel == "codex-auto-review" ? TokenLine(now) + "\n" : string.Empty), Utf8);
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        }

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(userTokenAt, CodexModel.Astra, "6"));
    }

    [Theory]
    [InlineData("unrelated.jsonl")]
    [InlineData("sessions-other/unrelated.jsonl")]
    [InlineData("archived_sessions-other/unrelated.jsonl")]
    [InlineData("other/sessions/unrelated.jsonl")]
    public async Task IgnoresLiveActivityOutsideSessionDirectories(string relativePath)
    {
        using var directory = new TemporaryDirectory("session-activity-directory-scope");
        var now = DateTimeOffset.Now;
        var session = SessionPath(directory);
        File.WriteAllText(session, ModelLine(now, "gpt-6-astra") + "\n" + TokenLine(now) + "\n", Utf8);
        var unrelated = directory.FilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now, CodexModel.Astra, "6"));

        var unexpectedActivity = new TaskCompletionSource<CodexSessionActivity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) =>
        {
            if (monitor.Current is { Model: CodexModel.Luna } unexpected)
            {
                unexpectedActivity.TrySetResult(unexpected);
            }
        };
        var activity = await ObserveChangeAsync(monitor, () =>
        {
            File.WriteAllText(unrelated,
                ModelLine(now.AddMinutes(1), "gpt-6-luna") + "\n" + TokenLine(now.AddMinutes(1)) + "\n", Utf8);
            File.AppendAllText(session, TokenLine(now.AddSeconds(1)) + "\n", Utf8);
        });

        Assert.Equal(new CodexSessionActivity(now.AddSeconds(1), CodexModel.Astra, "6"), activity);
        // The unrelated path may arrive in a later watcher batch than the legitimate append.
        var quietPeriod = Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        Assert.Same(quietPeriod, await Task.WhenAny(quietPeriod, unexpectedActivity.Task));
        await quietPeriod;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupKeepsRecentAgeAndUserSessionLimits(bool outsideAgeLimit)
    {
        using var directory = new TemporaryDirectory("session-activity-startup-limits");
        var now = DateTimeOffset.Now;
        var recentCount = outsideAgeLimit ? 1 : 12;
        for (var index = 0; index < recentCount; index++)
        {
            var path = SessionPath(directory, $"user-{index}.jsonl");
            File.WriteAllText(path, TokenLine(now.AddSeconds(-index)) + "\n", Utf8);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-index).UtcDateTime);
        }
        var oldSession = SessionPath(directory, "old.jsonl");
        File.WriteAllText(oldSession, TokenLine(now.AddMinutes(2)) + "\n", Utf8);
        File.SetLastWriteTimeUtc(oldSession, (outsideAgeLimit ? now.AddDays(-3) : now.AddHours(-1)).UtcDateTime);

        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);

        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now, CodexModel.Unknown));
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("archived_sessions")]
    public async Task TracksFilesMovedIntoAndOutOfSessionDirectories(string directoryName)
    {
        using var directory = new TemporaryDirectory("session-activity-move-scope");
        var now = DateTimeOffset.Now;
        var fallback = SessionPath(directory);
        File.WriteAllText(fallback, TokenLine(now) + "\n", Utf8);
        var outside = directory.FilePath("outside.jsonl");
        File.WriteAllText(outside,
            ModelLine(now.AddSeconds(1), "gpt-6-luna") + "\n" + TokenLine(now.AddSeconds(1)) + "\n", Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        var expectedFallback = new CodexSessionActivity(now, CodexModel.Unknown);
        await ObserveChangeAsync(monitor, expected: expectedFallback);
        var inside = directory.FilePath(Path.Combine(directoryName, "moved.jsonl"));
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);

        await ObserveChangeAsync(monitor, () => File.Move(outside, inside),
            new CodexSessionActivity(now.AddSeconds(1), CodexModel.Luna, "6"));
        await ObserveChangeAsync(monitor, () => File.Move(inside, outside), expectedFallback);
    }

    [Fact]
    public async Task ReplacesAnOlderKnownModelWithAnUnrecognizedCurrentModel()
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

        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now.AddSeconds(-1), CodexModel.Unknown));
        Assert.Equal(CodexModel.Unknown, monitor.Current.Model);
        Assert.Null(monitor.Current.ModelVersion);
    }

    [Fact]
    public async Task PublishesOnlyTheFinalActivityFromANewSession()
    {
        using var directory = new TemporaryDirectory("session-activity-batch");
        var session = SessionPath(directory);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        var now = DateTimeOffset.Now;
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<CodexSessionActivity>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) =>
        {
            var activity = monitor.Current;
            notifications.Enqueue(activity);
            if (activity.LastTokenAt == now.AddMilliseconds(100) && activity.Model == CodexModel.Luna)
            {
                observed.TrySetResult();
            }
        };
        var staged = directory.FilePath("staged.tmp");
        File.WriteAllText(staged,
            ModelLine(now, "gpt-6-astra") + "\n" +
            string.Concat(Enumerable.Range(1, 100).Select(index => TokenLine(now.AddMilliseconds(index)) + "\n")) +
            ModelLine(now.AddMilliseconds(101), "gpt-5.6-luna") + "\n", Utf8);

        File.Move(staged, session);

        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(new CodexSessionActivity(now.AddMilliseconds(100), CodexModel.Luna, "5.6"),
            Assert.Single(notifications));
    }

    [Fact]
    public async Task KeepsModelChangesBeforeALargeAppendTail()
    {
        using var directory = new TemporaryDirectory("session-activity-large-append");
        var session = SessionPath(directory);
        var now = DateTimeOffset.Now;
        File.WriteAllText(session, ModelLine(now.AddSeconds(-3), "gpt-6-astra") + "\n" +
            TokenLine(now.AddSeconds(-2)) + "\n", Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now.AddSeconds(-2), CodexModel.Astra, "6"));
        var observed = new TaskCompletionSource<CodexSessionActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) =>
        {
            if (monitor.Current.LastTokenAt == now)
            {
                observed.TrySetResult(monitor.Current);
            }
        };

        File.AppendAllText(session,
            ModelLine(now.AddSeconds(-1), "gpt-5.6-luna") + "\n" + PaddingLine + TokenLine(now) + "\n", Utf8);

        var activity = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(new CodexSessionActivity(now, CodexModel.Luna, "5.6"), activity);
    }

    [Fact]
    public async Task RetriesAnUnfinishedModelRecordOnTheNextAppend()
    {
        using var directory = new TemporaryDirectory("session-activity-partial-model");
        var session = SessionPath(directory);
        var now = DateTimeOffset.Now;
        File.WriteAllText(session, ModelLine(now.AddSeconds(-3), "gpt-6-astra") + "\r\n", Encoding.UTF8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        var model = ModelLine(now.AddSeconds(-1), "gpt-5.6-luna")
            .Replace("\"payload\":{", "\"payload\":{\"padding\":\"" + new string('x', 300_000) + "\",",
                StringComparison.Ordinal);

        var beforeCompletion = await ObserveChangeAsync(monitor, () => File.AppendAllText(session,
            TokenLine(now.AddSeconds(-2)) + "\r\n" + model[..^10], Utf8));
        Assert.Equal(new CodexSessionActivity(now.AddSeconds(-2), CodexModel.Astra, "6"), beforeCompletion);

        var afterCompletion = await ObserveChangeAsync(monitor, () => File.AppendAllText(session,
            model[^10..] + "\r\n{malformed}\r\n" + TokenLine(now) + "\r\n", Utf8));
        Assert.Equal(new CodexSessionActivity(now, CodexModel.Luna, "5.6"), afterCompletion);
    }

    [Fact]
    public async Task DiscardsSessionMetadataWhenAFileIsTruncated()
    {
        using var directory = new TemporaryDirectory("session-activity-truncated");
        var session = SessionPath(directory);
        var now = DateTimeOffset.Now;
        File.WriteAllText(session,
            ModelLine(now.AddSeconds(-2), "codex-auto-review") + "\n" + PaddingLine +
            TokenLine(now.AddSeconds(-1)) + "\n", Utf8);
        File.WriteAllText(SessionPath(directory, "user.jsonl"), TokenLine(now.AddSeconds(-3)) + "\n", Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now.AddSeconds(-3), CodexModel.Unknown));

        var activity = await ObserveChangeAsync(monitor,
            () => File.WriteAllText(session, TokenLine(now) + "\n", Utf8));

        Assert.Equal(new CodexSessionActivity(now, CodexModel.Unknown), activity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReclassifiesRewrittenSessionsThatHaveNotShrunk(bool sameLength)
    {
        using var directory = new TemporaryDirectory("session-activity-rewrite");
        var session = SessionPath(directory);
        var now = DateTimeOffset.Now;
        var original = ModelLine(now.AddSeconds(-3), "gpt-6-astra") + "\n" +
            TokenLine(now.AddSeconds(-2)) + "\n";
        var replacement = ModelLine(now.AddSeconds(-1), "codex-auto-review") + "\n" + TokenLine(now) + "\n";
        if (sameLength)
        {
            original = original.TrimEnd('\n').PadRight(replacement.Length - 1) + "\n";
        }

        File.WriteAllText(session, original, Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now.AddSeconds(-2), CodexModel.Astra, "6"));
        Assert.Equal(CodexModel.Astra, monitor.Current.Model);

        var activity = await ObserveChangeAsync(monitor, () => File.WriteAllText(session, replacement, Utf8));

        Assert.Equal(CodexSessionActivity.Empty, activity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallsBackToUserActivityWhenTheLatestSessionIsIdentifiedAsAReview(bool archiveSession)
    {
        using var directory = new TemporaryDirectory("session-activity-late-exclusion");
        var userSession = SessionPath(directory, "user.jsonl");
        var reviewSession = SessionPath(directory, "review.jsonl");
        var archivedSession = Path.Combine(directory.RootPath, "archived_sessions", "review.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(archivedSession)!);
        var now = DateTimeOffset.Now;
        File.WriteAllText(userSession,
            ModelLine(now.AddSeconds(-3), "gpt-6-astra") + "\n" + TokenLine(now.AddSeconds(-2)) + "\n", Utf8);
        File.WriteAllText(reviewSession, TokenLine(now.AddSeconds(-1)) + "\n", Utf8);
        using var monitor = new CodexSessionActivityMonitor(directory.RootPath);
        await ObserveChangeAsync(monitor, expected: new CodexSessionActivity(now.AddSeconds(-1), CodexModel.Unknown));
        Assert.Equal(now.AddSeconds(-1), monitor.Current.LastTokenAt);

        var activity = await ObserveChangeAsync(monitor, () =>
        {
            if (archiveSession)
            {
                File.Move(reviewSession, archivedSession);
            }

            File.AppendAllText(archiveSession ? archivedSession : reviewSession,
                ModelLine(now, "codex-auto-review") + "\n", Utf8);
        });

        Assert.Equal(new CodexSessionActivity(now.AddSeconds(-2), CodexModel.Astra, "6"), activity);
    }

    private static async Task<CodexSessionActivity> ObserveChangeAsync(
        CodexSessionActivityMonitor monitor,
        Action? change = null,
        CodexSessionActivity? expected = null)
    {
        var observed = new TaskCompletionSource<CodexSessionActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            var activity = monitor.Current;
            if (expected is null || activity == expected)
            {
                observed.TrySetResult(activity);
            }
        }
        monitor.Changed += OnChanged;
        try
        {
            change?.Invoke();
            if (expected is not null)
            {
                OnChanged(monitor, EventArgs.Empty);
            }
            return await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            monitor.Changed -= OnChanged;
        }
    }

    private static string PaddingLine => "{\"padding\":\"" + new string('x', 300_000) + "\"}\n";

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
