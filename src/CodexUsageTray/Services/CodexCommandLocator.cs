namespace CodexUsageTray;

internal static class CodexCommandLocator
{
    public static string Find()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_USAGE_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
            candidates.Add(Path.Combine(appData, "npm", "codex.exe"));
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.cmd"));
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.exe"));
        }

        var match = candidates.FirstOrDefault(File.Exists);
        return match ?? throw new InvalidOperationException(
            "Codex CLI was not found. Install it or set CODEX_USAGE_CODEX_PATH to codex.cmd or codex.exe.");
    }
}
