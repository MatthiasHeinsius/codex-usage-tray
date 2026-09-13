namespace CodexUsageTray.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string purpose)
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexUsageTray-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string FilePath(string name) => Path.Combine(RootPath, name);

    public void Dispose()
    {
        const int maximumAttempts = 10;
        for (var attempt = 1; Directory.Exists(RootPath); attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
        }
    }
}
