using System.Security.Cryptography;
using System.Text.Json;

namespace CodexUsageTray;

internal enum CodexModel
{
    Unknown,
    Luna,
    Terra,
    Sol,
    Astra
}

internal sealed record CodexSessionActivity(
    DateTimeOffset? LastTokenAt,
    CodexModel Model,
    string? ModelVersion = null)
{
    public static CodexSessionActivity Empty { get; } = new(null, CodexModel.Unknown);

    public string ModelDisplayName => Model == CodexModel.Unknown
        ? "unknown model"
        : ModelVersion is { } version
            ? $"GPT-{version} {Model}"
            : Model.ToString();
}

internal sealed class CodexSessionActivityMonitor : IDisposable
{
    private static readonly TimeSpan RecentFileAge = TimeSpan.FromDays(2);
    private readonly object gate = new();
    private readonly object readGate = new();
    private readonly HashSet<string> pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionState> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? watcher;
    private readonly System.Threading.Timer debounceTimer;
    private CodexSessionActivity current = CodexSessionActivity.Empty;
    private bool disposed;

    public CodexSessionActivityMonitor()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"))
    {
    }

    internal CodexSessionActivityMonitor(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        codexHome = Path.GetFullPath(codexHome);
        debounceTimer = new System.Threading.Timer(ProcessPendingPaths);
        if (Directory.Exists(codexHome))
        {
            watcher = new FileSystemWatcher(codexHome, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.EnableRaisingEvents = true;
        }

        ThreadPool.QueueUserWorkItem(_ => LoadRecentSessions(codexHome));
    }

    public event EventHandler? Changed;

    public CodexSessionActivity Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingPaths.Clear();
        }

        watcher?.Dispose();
        debounceTimer.Dispose();
    }

    private void LoadRecentSessions(string codexHome)
    {
        if (Volatile.Read(ref disposed))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - RecentFileAge;
        var candidates = new List<FileInfo>();
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(codexHome, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                candidates.AddRange(Directory
                    .EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.LastWriteTimeUtc >= cutoff));
            }
            catch (IOException)
            {
                // A session directory may change while Codex archives a conversation.
            }
            catch (UnauthorizedAccessException)
            {
                // One inaccessible directory should not disable the tray application.
            }
        }

        ReadSessions(candidates.OrderByDescending(file => file.LastWriteTimeUtc).Take(12)
            .Select(file => file.FullName));
    }

    private void OnFileChanged(object sender, FileSystemEventArgs eventArgs) => Queue(eventArgs.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs eventArgs)
    {
        Queue(eventArgs.OldFullPath);
        Queue(eventArgs.FullPath);
    }

    private void Queue(string path)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            pendingPaths.Add(path);
            debounceTimer.Change(25, Timeout.Infinite);
        }
    }

    private void ProcessPendingPaths(object? state)
    {
        string[] paths;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            paths = [.. pendingPaths];
            pendingPaths.Clear();
        }

        ReadSessions(paths);
    }

    private void ReadSessions(IEnumerable<string> paths)
    {
        // Timer callbacks can overlap; keep ingestion and publication in the same order.
        lock (readGate)
        {
            foreach (var path in paths)
            {
                if (Volatile.Read(ref disposed))
                {
                    return;
                }

                ReadSession(path);
            }

            var latest = sessions.Values
                .Where(session => session.LastTokenAt is not null && session.Model?.ExcludeSession != true)
                .MaxBy(session => session.LastTokenAt);
            var activity = latest is null
                ? CodexSessionActivity.Empty
                : new CodexSessionActivity(latest.LastTokenAt,
                    latest.Model?.Model ?? CodexModel.Unknown, latest.Model?.Version);
            lock (gate)
            {
                if (disposed || current == activity)
                {
                    return;
                }

                current = activity;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReadSession(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var endOffset = stream.Length;
            var state = sessions.GetValueOrDefault(path) ?? new SessionState(0, null, null);
            // ponytail: logs append. Check the last complete record for rewrites;
            // detecting arbitrary edits earlier in the file would require a full rescan.
            if (endOffset < state.Offset
                || (state.Fingerprint is { } fingerprint
                    && !fingerprint.AsSpan().SequenceEqual(ReadFingerprint(stream, state.RecordStart, state.Offset))))
            {
                state = new SessionState(0, null, null);
            }

            stream.Position = state.Offset;
            using var record = new MemoryStream();
            var buffer = new byte[16 * 1024];
            // Read the initial metadata once, then only appended complete records.
            // A fixed end keeps a busy session from extending this batch indefinitely.
            while (stream.Position < endOffset)
            {
                if (Volatile.Read(ref disposed))
                {
                    return;
                }

                var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, endOffset - stream.Position));
                if (count == 0)
                {
                    throw new IOException("The Codex session file was truncated during reading.");
                }

                var remaining = buffer.AsSpan(0, count);
                while (!remaining.IsEmpty)
                {
                    var newline = remaining.IndexOf((byte)'\n');
                    var length = newline < 0 ? remaining.Length : newline;
                    record.Write(remaining[..length]);
                    if (newline < 0)
                    {
                        break;
                    }

                    state = ReadRecord(state, record.GetBuffer().AsMemory(0, (int)record.Length));
                    record.SetLength(0);
                    remaining = remaining[(newline + 1)..];
                    state = state with
                    {
                        RecordStart = state.Offset,
                        Offset = stream.Position - remaining.Length
                    };
                }
            }

            // Commit metadata and progress together; retry unfinished records on the next append.
            if (state.Offset > 0)
            {
                state = state with { Fingerprint = ReadFingerprint(stream, state.RecordStart, state.Offset) };
            }

            sessions[path] = state;
        }
        catch (IOException exception)
        {
            if (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                sessions.Remove(path);
            }

            // The next write event retries files that are temporarily unavailable or being moved.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore session files that cannot be read by this process.
        }
    }

    private static byte[] ReadFingerprint(FileStream stream, long start, long end)
    {
        stream.Position = start;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[(int)Math.Min(16 * 1024, end - start)];
        while (stream.Position < end)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - stream.Position));
            if (count == 0)
            {
                throw new IOException("The Codex session file was truncated during reading.");
            }

            hash.AppendData(buffer, 0, count);
        }

        return hash.GetHashAndReset();
    }

    private static SessionState ReadRecord(SessionState state, ReadOnlyMemory<byte> line)
    {
        try
        {
            if (line.Span.StartsWith("\uFEFF"u8))
            {
                line = line[3..];
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryTimestamp(root, out var observedAt))
            {
                return state;
            }

            if (TryModel(root, out var model, out var modelVersion, out var excludeSession)
                && (state.Model is null || observedAt >= state.Model.ObservedAt))
            {
                state = state with
                {
                    Model = new ObservedModel(observedAt, model, modelVersion, excludeSession)
                };
            }

            if (IsTokenUsageRecord(root) && (state.LastTokenAt is null || observedAt > state.LastTokenAt))
            {
                state = state with { LastTokenAt = observedAt };
            }
        }
        catch (JsonException)
        {
            // Ignore malformed complete records.
        }
        catch (InvalidOperationException)
        {
            // Invalid JSON strings can still fail access after parsing.
        }

        return state;
    }

    private static bool TryTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var element)
            && element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), out timestamp);
    }

    private static bool IsTokenUsageRecord(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = type.GetString();
        if (value == "token_usage_record")
        {
            return true;
        }

        return value == "event_msg"
            && root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("type", out var payloadType)
            && payloadType.ValueKind == JsonValueKind.String
            && payloadType.GetString() == "token_count";
    }

    private static bool TryModel(
        JsonElement root,
        out CodexModel model,
        out string? version,
        out bool excludeSession)
    {
        model = CodexModel.Unknown;
        version = null;
        excludeSession = false;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryModelName(payload, "model", out model, out version, out excludeSession))
        {
            return true;
        }

        foreach (var containerName in new[] { "thread_settings", "turn_context" })
        {
            if (payload.TryGetProperty(containerName, out var container)
                && container.ValueKind == JsonValueKind.Object
                && TryModelName(container, "model", out model, out version, out excludeSession))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryModelName(
        JsonElement element,
        string propertyName,
        out CodexModel model,
        out string? version,
        out bool excludeSession)
    {
        model = CodexModel.Unknown;
        version = null;
        excludeSession = false;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = property.GetString();
        if (value is null)
        {
            return false;
        }

        excludeSession = value.Equals("codex-auto-review", StringComparison.OrdinalIgnoreCase);
        model = value.ToLowerInvariant() switch
        {
            var name when name.Contains("luna", StringComparison.Ordinal) => CodexModel.Luna,
            var name when name.Contains("terra", StringComparison.Ordinal) => CodexModel.Terra,
            var name when name.Contains("astra", StringComparison.Ordinal) => CodexModel.Astra,
            var name when name.Contains("sol", StringComparison.Ordinal) => CodexModel.Sol,
            _ => CodexModel.Unknown
        };
        if (model != CodexModel.Unknown && value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = value.Split('-');
            if (parts.Length > 2)
            {
                var candidate = parts[1].Length > 0 && char.IsAsciiDigit(parts[1][0])
                    ? parts[1] : parts[2];
                if (candidate.Length > 0
                    && candidate.AsSpan().IndexOfAnyExcept("0123456789.".AsSpan()) < 0)
                {
                    version = candidate;
                }
            }
        }
        return true;
    }

    private sealed record SessionState(
        long Offset,
        ObservedModel? Model,
        DateTimeOffset? LastTokenAt,
        long RecordStart = 0,
        byte[]? Fingerprint = null);

    private sealed record ObservedModel(
        DateTimeOffset ObservedAt,
        CodexModel Model,
        string? Version,
        bool ExcludeSession);
}
