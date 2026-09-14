using System.Text;
using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class LocalTokenUsageReaderTests
{
    private static readonly DateTimeOffset September7 = new(
        2026,
        9,
        7,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly Encoding Utf8WithoutByteOrderMark = new UTF8Encoding(false);

    [Fact]
    public void ReadTodayAccumulatesCompletedRecordsAcrossFileAppends()
    {
        using var directory = new TemporaryDirectory("local-usage-appends");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllText(sessionPath, UsageLine(September7, 1_200) + "\n", Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(1_200, reader.ReadToday(September7, TestContext.Current.CancellationToken));

        File.AppendAllText(sessionPath, UsageLine(September7, 800), Utf8WithoutByteOrderMark);
        Assert.Equal(1_200, reader.ReadToday(September7, TestContext.Current.CancellationToken));

        File.AppendAllText(sessionPath, "\n", Utf8WithoutByteOrderMark);
        Assert.Equal(2_000, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        Assert.Equal(2_000, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadTodayRebuildsCachedTotalAfterFileIsTruncated(bool readWhileEmpty)
    {
        using var directory = new TemporaryDirectory("local-usage-truncation");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllText(sessionPath, UsageLine(September7, 123_456_789) + "\n", Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);
        Assert.Equal(123_456_789, reader.ReadToday(September7, TestContext.Current.CancellationToken));

        if (readWhileEmpty)
        {
            File.WriteAllText(sessionPath, string.Empty, Utf8WithoutByteOrderMark);
            Assert.Null(reader.ReadToday(September7, TestContext.Current.CancellationToken));
        }

        File.WriteAllText(sessionPath, UsageLine(September7, 1) + "\n", Utf8WithoutByteOrderMark);

        Assert.Equal(1, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadTodayClearsCachedFilesWhenTheLocalDateChanges()
    {
        using var directory = new TemporaryDirectory("local-usage-date");
        var sessionPath = ArchivePath(directory, "session.jsonl");
        File.WriteAllText(sessionPath, UsageLine(September7, 1_200) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(sessionPath, new DateTime(2026, 9, 7, 13, 0, 0));
        var reader = new LocalTokenUsageReader(directory.RootPath);
        Assert.Equal(1_200, reader.ReadToday(September7, TestContext.Current.CancellationToken));

        var nextDay = September7.AddDays(1);
        File.WriteAllText(sessionPath, UsageLine(nextDay, 8_000) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(sessionPath, new DateTime(2026, 9, 8, 13, 0, 0));

        Assert.Equal(8_000, reader.ReadToday(nextDay, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadTodayIncludesRecentArchivedSessionsAndIgnoresOlderArchiveFiles()
    {
        using var directory = new TemporaryDirectory("local-usage-archives");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllText(sessionPath, UsageLine(September7, 1_200) + "\n", Utf8WithoutByteOrderMark);
        var recentArchive = ArchivePath(directory, "nested", "recent.jsonl");
        File.WriteAllText(recentArchive, UsageLine(September7, 800) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(recentArchive, new DateTime(2026, 9, 7, 13, 0, 0));
        var oldArchive = ArchivePath(directory, "old.jsonl");
        File.WriteAllText(oldArchive, UsageLine(September7, 9_999) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(oldArchive, new DateTime(2026, 9, 6, 23, 59, 59));
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(2_000, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadTodayIncludesAnActiveSessionThatContinuesPastMidnight()
    {
        using var directory = new TemporaryDirectory("local-usage-cross-midnight");
        var sessionPath = SessionPath(directory, September7.AddDays(-1));
        File.WriteAllText(sessionPath, UsageLine(September7, 1_200) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(sessionPath, new DateTime(2026, 9, 7, 13, 0, 0));
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(1_200, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadTodayIgnoresOtherDatesRecordTypesAndMalformedJson()
    {
        using var directory = new TemporaryDirectory("local-usage-filtering");
        var lines = new[]
        {
            UsageLine(September7, 1_200),
            UsageLine(September7.AddDays(-1), 9_999),
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{}}",
            "not JSON"
        };
        File.WriteAllLines(SessionPath(directory, September7), lines, Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(1_200, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadTodayReturnsNullWhenNoMatchingRecordExists()
    {
        using var directory = new TemporaryDirectory("local-usage-not-found");
        File.WriteAllText(
            SessionPath(directory, September7),
            UsageLine(September7.AddDays(-1), 100) + "\n",
            Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Null(reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":123}")]
    [InlineData("{\"type\":\"token_usage_record\",\"timestamp\":123}")]
    [InlineData("{\"payload\":null}")]
    [InlineData("{\"payload\":{\"usage\":[]}}")]
    [InlineData("{\"payload\":{\"usage\":{\"total_tokens\":\"42\"}}}")]
    [InlineData("{\"payload\":{\"usage\":{\"total_tokens\":-1}}}")]
    [InlineData("{\"payload\":{\"usage\":{\"total_tokens\":9223372036854775808}}}")]
    public void ReadTodaySkipsInvalidRecordsWithoutLosingHealthyRecords(string invalidJson)
    {
        using var directory = new TemporaryDirectory("local-usage-invalid-record");
        var sessionPath = SessionPath(directory, September7);
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(invalidJson);
        if (invalid is System.Text.Json.Nodes.JsonObject record)
        {
            record.TryAdd("type", "token_usage_record");
            record.TryAdd("timestamp", System.Text.Json.Nodes.JsonValue.Create(September7));
        }

        File.WriteAllLines(sessionPath,
            [UsageLine(September7, 10), invalid?.ToJsonString() ?? "null", UsageLine(September7, 42)],
            Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(52, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        Assert.Equal(52, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        File.AppendAllText(sessionPath, UsageLine(September7, 8) + "\n", Utf8WithoutByteOrderMark);
        Assert.Equal(60, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadTodayPreservesUtf8AndRecordsAcrossBufferAndAppendBoundaries(bool splitAppend)
    {
        using var directory = new TemporaryDirectory("local-usage-utf8");
        var sessionPath = SessionPath(directory, September7);
        // Put a multibyte character across a read-buffer edge, optionally splitting the append there too.
        var prefix = "{\"padding\":\"" + new string('x', 16_384 - 13);
        var suffix = "\"," + UsageLine(September7, 42)[1..] + "\r\n";
        var bytes = Utf8WithoutByteOrderMark.GetBytes(prefix + "€" + suffix);
        var split = Utf8WithoutByteOrderMark.GetByteCount(prefix) + 1;
        File.WriteAllBytes(sessionPath, splitAppend ? bytes[..split] : bytes);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        if (splitAppend)
        {
            Assert.Null(reader.ReadToday(September7, TestContext.Current.CancellationToken));
            using var append = new FileStream(sessionPath, FileMode.Append);
            append.Write(bytes.AsSpan(split));
        }

        Assert.Equal(42, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        Assert.Equal(42, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadTodaySkipsInvalidUtf8WithoutLosingTheFollowingRecord()
    {
        using var directory = new TemporaryDirectory("local-usage-invalid-utf8");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllBytes(sessionPath,
        [
            .. Utf8WithoutByteOrderMark.GetBytes("{\"type\":\"token_usage_record\",\"timestamp\":\""),
            0xff,
            .. Utf8WithoutByteOrderMark.GetBytes("\"}\n" + UsageLine(September7, 42) + "\n")
        ]);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(42, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"type\":\"token_usage_record\",\"timestamp\":\"\\uD800\"}")]
    [InlineData("{\"type\":\"\\uDC00\"}")]
    public void ReadTodaySkipsInvalidStringEscapesWithoutLosingTheFollowingRecord(string invalidJson)
    {
        using var directory = new TemporaryDirectory("local-usage-invalid-escape");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllLines(sessionPath, [invalidJson, UsageLine(September7, 42)], Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(42, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        Assert.Equal(42, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CanceledReadLeavesCachedAndAppendedRecordsAvailableForRetry()
    {
        using var directory = new TemporaryDirectory("local-usage-cancellation");
        var sessionPath = SessionPath(directory, September7);
        var line = UsageLine(September7, 1);
        File.WriteAllText(sessionPath, line + "\n", Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);
        Assert.Equal(1, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        File.AppendAllText(sessionPath, UsageLine(September7, 42) + "\n", Utf8WithoutByteOrderMark);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var failure = Assert.Throws<OperationCanceledException>(() => reader.ReadToday(September7, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);

        Assert.Equal(43, reader.ReadToday(September7, TestContext.Current.CancellationToken));
        Assert.Equal(43, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FailedAccumulationDoesNotCommitPartialProgress()
    {
        using var directory = new TemporaryDirectory("local-usage-overflow");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllLines(sessionPath, [UsageLine(September7, 10), UsageLine(September7, long.MaxValue)],
            Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Throws<OverflowException>(() => reader.ReadToday(September7, TestContext.Current.CancellationToken));
        Assert.Throws<OverflowException>(() => reader.ReadToday(September7, TestContext.Current.CancellationToken));
        File.WriteAllText(sessionPath, UsageLine(September7, 42) + "\n", Utf8WithoutByteOrderMark);
        Assert.Equal(42, reader.ReadToday(September7, TestContext.Current.CancellationToken));
    }

    private static string SessionPath(TemporaryDirectory directory, DateTimeOffset timestamp)
    {
        var sessionDirectory = Path.Combine(
            directory.RootPath,
            "sessions",
            timestamp.Year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            timestamp.Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            timestamp.Day.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(sessionDirectory);
        return Path.Combine(sessionDirectory, "session.jsonl");
    }

    private static string ArchivePath(TemporaryDirectory directory, params string[] relativePath)
    {
        var path = Path.Combine([directory.RootPath, "archived_sessions", .. relativePath]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string UsageLine(DateTimeOffset timestamp, long tokens) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "token_usage_record",
            payload = new { usage = new { total_tokens = tokens } }
        });
}
