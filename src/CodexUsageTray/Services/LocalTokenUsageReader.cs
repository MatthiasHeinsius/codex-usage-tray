using System.Text.Json;

namespace CodexUsageTray;

internal interface ILocalTokenUsageReader
{
    long? ReadToday(DateTimeOffset now, CancellationToken cancellationToken = default);
}

internal sealed class LocalTokenUsageReader : ILocalTokenUsageReader
{
    private readonly string codexHome;
    private readonly Dictionary<string, FileState> files = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly cachedDate;

    public LocalTokenUsageReader()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"))
    {
    }

    internal LocalTokenUsageReader(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        this.codexHome = Path.GetFullPath(codexHome);
    }

    public long? ReadToday(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localDate = DateOnly.FromDateTime(now.LocalDateTime);
        if (cachedDate != localDate)
        {
            cachedDate = localDate;
            files.Clear();
        }

        long total = 0;
        var found = false;
        foreach (var path in FindCandidateFiles(codexHome, localDate, cancellationToken))
        {
            var state = ReadNewRecords(path, localDate, cancellationToken);
            total = checked(total + state.Tokens);
            found |= state.FoundUsage;
        }

        return found ? total : null;
    }

    private static IEnumerable<string> FindCandidateFiles(
        string codexHome,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        var localStart = localDate.ToDateTime(TimeOnly.MinValue);
        foreach (var root in new[] { "sessions", "archived_sessions" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(codexHome, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.GetLastWriteTime(path) >= localStart)
                {
                    yield return path;
                }
            }
        }
    }

    private FileState ReadNewRecords(string path, DateOnly localDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!files.TryGetValue(path, out var state))
        {
            state = new FileState();
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var endOffset = stream.Length;
        if (endOffset < state.Offset)
        {
            state = new FileState();
        }

        if (endOffset == state.Offset)
        {
            files[path] = state;
            return state;
        }

        stream.Position = state.Offset;
        using var record = new MemoryStream();
        record.Write(state.PartialLine);
        var buffer = new byte[16 * 1024];
        var tokens = state.Tokens;
        var foundUsage = state.FoundUsage;
        // Read only the bytes present at the start, even if Codex keeps appending.
        while (stream.Position < endOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, endOffset - stream.Position));
            if (count == 0)
            {
                throw new IOException("The Codex session file was truncated during reading.");
            }

            var remaining = buffer.AsSpan(0, count);
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newline = remaining.IndexOf((byte)'\n');
                var length = newline < 0 ? remaining.Length : newline;
                record.Write(remaining[..length]);
                if (newline < 0)
                {
                    break;
                }

                if (TryReadUsage(record.GetBuffer().AsMemory(0, (int)record.Length), localDate, out var recordTokens))
                {
                    tokens = checked(tokens + recordTokens);
                    foundUsage = true;
                }

                record.SetLength(0);
                remaining = remaining[(newline + 1)..];
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Commit progress and totals together. Canceled or failed reads can safely retry.
        state = new FileState(endOffset, record.ToArray(), tokens, foundUsage);
        files[path] = state;
        return state;
    }

    private static bool TryReadUsage(ReadOnlyMemory<byte> line, DateOnly localDate, out long tokens)
    {
        tokens = 0;
        if (line.IsEmpty)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "token_usage_record"
                || !root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timestampElement.GetString(), out var timestamp)
                || DateOnly.FromDateTime(timestamp.LocalDateTime) != localDate
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object
                || !usage.TryGetProperty("total_tokens", out var total)
                || total.ValueKind != JsonValueKind.Number
                || !total.TryGetInt64(out tokens)
                || tokens < 0)
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // JSON string access can reject invalid UTF-8 or escaped surrogates after parsing succeeded.
            return false;
        }
    }

    private sealed record FileState(long Offset, byte[] PartialLine, long Tokens, bool FoundUsage)
    {
        public FileState() : this(0, [], 0, false)
        {
        }
    }
}
