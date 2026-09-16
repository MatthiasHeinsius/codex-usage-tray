using System.Text;
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
    CodexModel Model)
{
    public static CodexSessionActivity Empty { get; } = new(null, CodexModel.Unknown);
}

internal sealed class CodexSessionActivityMonitor : IDisposable
{
    private const int TailLength = 256 * 1024;
    private static readonly TimeSpan RecentFileAge = TimeSpan.FromDays(2);
    private readonly object gate = new();
    private readonly HashSet<string> pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObservedModel> modelsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? watcher;
    private readonly System.Threading.Timer debounceTimer;
    private CodexSessionActivity current = CodexSessionActivity.Empty;
    private string? activePath;
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
            watcher.Renamed += OnFileRenamed;
            watcher.EnableRaisingEvents = true;
        }

        LoadRecentSessions(codexHome);
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

        foreach (var file in candidates.OrderByDescending(file => file.LastWriteTimeUtc).Take(12))
        {
            ReadTail(file.FullName);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs eventArgs) => Queue(eventArgs.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs eventArgs) => Queue(eventArgs.FullPath);

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

        foreach (var path in paths)
        {
            ReadTail(path);
        }
    }

    private void ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > TailLength)
            {
                stream.Position = stream.Length - TailLength;
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: false);
            if (stream.Position > 0)
            {
                reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                ReadRecord(path, line);
            }
        }
        catch (IOException)
        {
            // The next write event retries files that are temporarily unavailable or being moved.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore session files that cannot be read by this process.
        }
    }

    private void ReadRecord(string path, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryTimestamp(root, out var observedAt))
            {
                return;
            }

            var tokenAt = IsTokenUsageRecord(root) ? observedAt : (DateTimeOffset?)null;
            var hasModel = TryModel(root, out var model);
            var changed = false;
            lock (gate)
            {
                if (hasModel
                    && (!modelsByPath.TryGetValue(path, out var previousModel)
                        || observedAt >= previousModel.ObservedAt))
                {
                    modelsByPath[path] = new ObservedModel(observedAt, model);
                    if (string.Equals(activePath, path, StringComparison.OrdinalIgnoreCase)
                        && current.Model != model)
                    {
                        current = current with { Model = model };
                        changed = true;
                    }
                }

                if (tokenAt is { } timestamp
                    && (current.LastTokenAt is null || timestamp > current.LastTokenAt))
                {
                    activePath = path;
                    current = new CodexSessionActivity(
                        timestamp,
                        modelsByPath.GetValueOrDefault(path)?.Model ?? CodexModel.Unknown);
                    changed = true;
                }
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (JsonException)
        {
            // Partial and malformed lines are harmless; a completed append triggers another event.
        }
        catch (InvalidOperationException)
        {
            // Invalid JSON strings can still fail access after parsing.
        }
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

    private static bool TryModel(JsonElement root, out CodexModel model)
    {
        model = CodexModel.Unknown;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryModelName(payload, "model", out model))
        {
            return true;
        }

        foreach (var containerName in new[] { "thread_settings", "turn_context" })
        {
            if (payload.TryGetProperty(containerName, out var container)
                && container.ValueKind == JsonValueKind.Object
                && TryModelName(container, "model", out model))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryModelName(JsonElement element, string propertyName, out CodexModel model)
    {
        model = CodexModel.Unknown;
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

        model = value.ToLowerInvariant() switch
        {
            var name when name.Contains("luna", StringComparison.Ordinal) => CodexModel.Luna,
            var name when name.Contains("terra", StringComparison.Ordinal) => CodexModel.Terra,
            var name when name.Contains("astra", StringComparison.Ordinal) => CodexModel.Astra,
            var name when name.Contains("sol", StringComparison.Ordinal) => CodexModel.Sol,
            _ => CodexModel.Unknown
        };
        return true;
    }

    private sealed record ObservedModel(DateTimeOffset ObservedAt, CodexModel Model);
}
