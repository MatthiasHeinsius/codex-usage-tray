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

        Assert.Equal(1_200, reader.ReadToday(September7));

        File.AppendAllText(sessionPath, UsageLine(September7, 800), Utf8WithoutByteOrderMark);
        Assert.Equal(1_200, reader.ReadToday(September7));

        File.AppendAllText(sessionPath, "\n", Utf8WithoutByteOrderMark);
        Assert.Equal(2_000, reader.ReadToday(September7));
        Assert.Equal(2_000, reader.ReadToday(September7));
    }

    [Fact]
    public void ReadTodayRebuildsCachedTotalAfterFileIsTruncated()
    {
        using var directory = new TemporaryDirectory("local-usage-truncation");
        var sessionPath = SessionPath(directory, September7);
        File.WriteAllText(sessionPath, UsageLine(September7, 123_456_789) + "\n", Utf8WithoutByteOrderMark);
        var reader = new LocalTokenUsageReader(directory.RootPath);
        Assert.Equal(123_456_789, reader.ReadToday(September7));

        File.WriteAllText(sessionPath, UsageLine(September7, 1) + "\n", Utf8WithoutByteOrderMark);

        Assert.Equal(1, reader.ReadToday(September7));
    }

    [Fact]
    public void ReadTodayClearsCachedFilesWhenTheLocalDateChanges()
    {
        using var directory = new TemporaryDirectory("local-usage-date");
        var sessionPath = ArchivePath(directory, "session.jsonl");
        File.WriteAllText(sessionPath, UsageLine(September7, 1_200) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(sessionPath, new DateTime(2026, 9, 7, 13, 0, 0));
        var reader = new LocalTokenUsageReader(directory.RootPath);
        Assert.Equal(1_200, reader.ReadToday(September7));

        var nextDay = September7.AddDays(1);
        File.WriteAllText(sessionPath, UsageLine(nextDay, 8_000) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(sessionPath, new DateTime(2026, 9, 8, 13, 0, 0));

        Assert.Equal(8_000, reader.ReadToday(nextDay));
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

        Assert.Equal(2_000, reader.ReadToday(September7));
    }

    [Fact]
    public void ReadTodayIncludesAnActiveSessionThatContinuesPastMidnight()
    {
        using var directory = new TemporaryDirectory("local-usage-cross-midnight");
        var sessionPath = SessionPath(directory, September7.AddDays(-1));
        File.WriteAllText(sessionPath, UsageLine(September7, 1_200) + "\n", Utf8WithoutByteOrderMark);
        File.SetLastWriteTime(sessionPath, new DateTime(2026, 9, 7, 13, 0, 0));
        var reader = new LocalTokenUsageReader(directory.RootPath);

        Assert.Equal(1_200, reader.ReadToday(September7));
    }

    [Fact]
    public void SumLinesForDateIgnoresOtherDatesRecordTypesAndMalformedJson()
    {
        var lines = new[]
        {
            UsageLine(September7, 1_200),
            UsageLine(September7.AddDays(-1), 9_999),
            "{\"timestamp\":\"2026-09-07T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{}}",
            "not JSON"
        };

        var result = LocalTokenUsageReader.SumLinesForDate(lines, new DateOnly(2026, 9, 7));

        Assert.True(result.Found);
        Assert.Equal(1_200, result.Tokens);
    }

    [Fact]
    public void SumLinesForDateReturnsNotFoundWhenNoMatchingRecordExists()
    {
        var result = LocalTokenUsageReader.SumLinesForDate(
            [UsageLine(September7.AddDays(-1), 100)],
            new DateOnly(2026, 9, 7));

        Assert.False(result.Found);
        Assert.Equal(0, result.Tokens);
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
