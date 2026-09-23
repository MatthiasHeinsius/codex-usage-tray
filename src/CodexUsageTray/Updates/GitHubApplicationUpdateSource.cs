using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageTray;

internal sealed class GitHubApplicationUpdateSource : IApplicationUpdateSource
{
    private const string ExecutableAssetName = "CodexUsageTray.exe";
    private const int MaxReleaseMetadataBytes = 1024 * 1024;
    private const long MaxExecutableBytes = 250L * 1024 * 1024;
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/MatthiasHeinsius/codex-usage-tray/releases/latest");
    private readonly HttpClient httpClient;
    private readonly Version currentVersion;
    private readonly string updateDirectory;
    private readonly Func<Version, string, CancellationToken, Task> verifyRelease;

    internal GitHubApplicationUpdateSource(
        HttpClient httpClient,
        Version currentVersion,
        string updateDirectory,
        Func<Version, string, CancellationToken, Task>? verifyRelease = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(updateDirectory);
        this.httpClient = httpClient;
        this.currentVersion = currentVersion;
        this.updateDirectory = Path.GetFullPath(updateDirectory);
        this.verifyRelease = verifyRelease
            ?? new GitHubReleaseAttestationVerifier(httpClient).VerifyAsync;
    }

    public Version CurrentVersion => currentVersion;

    internal static GitHubApplicationUpdateSource CreateDefault()
    {
        var version = typeof(GitHubApplicationUpdateSource).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The application version is unavailable.");
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"CodexUsageTray/{version.ToString(3)}");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubApplicationUpdateSource(client, version, Path.GetTempPath());
    }

    public async Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var releaseBytes = await DownloadLimitedBytesAsync(
            httpClient, LatestReleaseUri, MaxReleaseMetadataBytes, cancellationToken).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(releaseBytes)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");

        var latestVersion = ParseVersion(release.TagName);
        if (latestVersion <= currentVersion)
        {
            return null;
        }

        var executableAsset = FindAsset(release, ExecutableAssetName);
        return new AvailableApplicationUpdate(
            latestVersion,
            executableAsset.DownloadUrl);
    }

    public async Task<StagedApplicationUpdate> DownloadAsync(
        AvailableApplicationUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(httpClient.Timeout);
        var stagedPath = CreateStagedPath(update.Version);
        try
        {
            await DownloadFileAsync(update.ExecutableDownloadUrl, stagedPath, deadline.Token)
                .ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(stagedPath, deadline.Token)
                .ConfigureAwait(false);
            await verifyRelease(update.Version, actualHash, deadline.Token).ConfigureAwait(false);
            return new StagedApplicationUpdate(update.Version, stagedPath, actualHash);
        }
        catch (Exception exception)
        {
            ApplicationUpdateFiles.TryDelete(stagedPath);
            if (exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested
                && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("The update download timed out. Try again.", exception);
            }

            throw;
        }
    }

    private static Version ParseVersion(string tagName)
    {
        if (tagName.StartsWith('v')
            && Version.TryParse(tagName[1..], out var version)
            && version.Build >= 0
            && version.Revision < 0
            && tagName == $"v{version.ToString(3)}")
        {
            return version;
        }

        throw new InvalidDataException($"GitHub release tag '{tagName}' is not a valid version.");
    }

    private static GitHubAsset FindAsset(GitHubRelease release, string name) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"GitHub release {release.TagName} does not contain {name}.");

    internal static async Task<byte[]> DownloadLimitedBytesAsync(
        HttpClient httpClient,
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("An update response exceeds its size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)target.Length)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return target.ToArray();
            }

            if (target.Length + count > maximumBytes)
            {
                throw new InvalidDataException("An update response exceeds its size limit.");
            }

            target.Write(buffer, 0, count);
        }
    }

    private async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxExecutableBytes)
        {
            throw new InvalidDataException("The update executable exceeds its size limit.");
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81_920];
        long downloaded = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            downloaded += count;
            if (downloaded > MaxExecutableBytes)
            {
                throw new InvalidDataException("The update executable exceeds its size limit.");
            }

            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private string CreateStagedPath(Version version)
    {
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"CodexUsageTray-{version}-update-{Guid.NewGuid():N}.tmp");
        return Path.Combine(updateDirectory, fileName);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri DownloadUrl);
}
