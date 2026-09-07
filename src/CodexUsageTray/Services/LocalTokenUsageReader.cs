using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray;

internal sealed class LocalTokenUsageReader
{
    private readonly Dictionary<string, FileState> files = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly cachedDate;

    public long? ReadToday(DateTimeOffset now)
    {
        var localDate = DateOnly.FromDateTime(now.LocalDateTime);
        if (cachedDate != localDate)
        {
            cachedDate = localDate;
            files.Clear();
        }

        var paths = FindCandidateFiles(localDate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long total = 0;
        var found = false;
        foreach (var path in paths)
        {
            var state = ReadNewRecords(path, localDate);
            total = checked(total + state.Tokens);
            found |= state.FoundUsage;
        }

        return found ? total : null;
    }

    internal static (long Tokens, bool Found) SumLinesForDate(IEnumerable<string> lines, DateOnly localDate)
    {
        long total = 0;
        var found = false;
        foreach (var line in lines)
        {
            if (TryReadUsage(line, localDate, out var tokens))
            {
                total = checked(total + tokens);
                found = true;
            }
        }

        return (total, found);
    }

    private static IEnumerable<string> FindCandidateFiles(DateOnly localDate)
    {
        var codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var sessionDirectory = Path.Combine(
            codexHome,
            "sessions",
            localDate.Year.ToString("0000", CultureInfo.InvariantCulture),
            localDate.Month.ToString("00", CultureInfo.InvariantCulture),
            localDate.Day.ToString("00", CultureInfo.InvariantCulture));
        if (Directory.Exists(sessionDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(sessionDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }

        var archivedDirectory = Path.Combine(codexHome, "archived_sessions");
        if (!Directory.Exists(archivedDirectory))
        {
            yield break;
        }

        var localStart = localDate.ToDateTime(TimeOnly.MinValue);
        foreach (var path in Directory.EnumerateFiles(archivedDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTime(path) >= localStart)
            {
                yield return path;
            }
        }
    }

    private FileState ReadNewRecords(string path, DateOnly localDate)
    {
        if (!files.TryGetValue(path, out var state))
        {
            state = new FileState();
            files[path] = state;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
        {
            state.Reset();
        }

        if (stream.Length == state.Offset)
        {
            return state;
        }

        stream.Position = state.Offset;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        state.Offset = stream.Length;
        var appended = Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
        var combined = state.PartialLine + appended;
        var lines = combined.Split('\n');
        state.PartialLine = combined.EndsWith('\n') ? string.Empty : lines[^1];
        var completeLineCount = state.PartialLine.Length == 0 ? lines.Length : lines.Length - 1;
        for (var index = 0; index < completeLineCount; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (TryReadUsage(line, localDate, out var tokens))
            {
                state.Tokens = checked(state.Tokens + tokens);
                state.FoundUsage = true;
            }
        }

        return state;
    }

    private static bool TryReadUsage(string line, DateOnly localDate, out long tokens)
    {
        tokens = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "token_usage_record"
                || !root.TryGetProperty("timestamp", out var timestampElement)
                || !DateTimeOffset.TryParse(timestampElement.GetString(), out var timestamp)
                || DateOnly.FromDateTime(timestamp.LocalDateTime) != localDate
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("usage", out var usage)
                || !usage.TryGetProperty("total_tokens", out var total)
                || !total.TryGetInt64(out tokens))
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class FileState
    {
        public long Offset { get; set; }
        public string PartialLine { get; set; } = string.Empty;
        public long Tokens { get; set; }
        public bool FoundUsage { get; set; }

        public void Reset()
        {
            Offset = 0;
            PartialLine = string.Empty;
            Tokens = 0;
            FoundUsage = false;
        }
    }
}
