using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageTray;

internal sealed class GitHubApplicationUpdateSource : IApplicationUpdateSource
{
    private const string ExecutableAssetName = "CodexUsageTray.exe";
    private const string ChecksumAssetName = "SHA256SUMS.txt";
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/MatthiasHeinsius/codex-usage-tray/releases/latest");
    private readonly HttpClient httpClient;
    private readonly Version currentVersion;
    private readonly string updateDirectory;

    internal GitHubApplicationUpdateSource(HttpClient httpClient, Version currentVersion, string updateDirectory)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(updateDirectory);
        this.httpClient = httpClient;
        this.currentVersion = currentVersion;
        this.updateDirectory = Path.GetFullPath(updateDirectory);
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
        using var releaseResponse = await httpClient.GetAsync(LatestReleaseUri, cancellationToken)
            .ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            releaseStream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");

        var latestVersion = ParseVersion(release.TagName);
        if (latestVersion <= currentVersion)
        {
            return null;
        }

        var executableAsset = FindAsset(release, ExecutableAssetName);
        var checksumAsset = FindAsset(release, ChecksumAssetName);
        return new AvailableApplicationUpdate(
            latestVersion,
            executableAsset.DownloadUrl,
            checksumAsset.DownloadUrl);
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
            var expectedHash = await DownloadExpectedHashAsync(update.ChecksumDownloadUrl, deadline.Token)
                .ConfigureAwait(false);
            await DownloadFileAsync(update.ExecutableDownloadUrl, stagedPath, deadline.Token)
                .ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(stagedPath, deadline.Token)
                .ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The staged executable failed its SHA-256 check. Expected {expectedHash}, got {actualHash}.");
            }

            return new StagedApplicationUpdate(update.Version, stagedPath);
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
        var value = tagName.StartsWith('v') ? tagName[1..] : tagName;
        return Version.TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"GitHub release tag '{tagName}' is not a valid version.");
    }

    private static GitHubAsset FindAsset(GitHubRelease release, string name) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"GitHub release {release.TagName} does not contain {name}.");

    private async Task<string> DownloadExpectedHashAsync(Uri uri, CancellationToken cancellationToken)
    {
        var contents = await httpClient.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        foreach (var line in contents.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2
                && fields[0].Length == 64
                && fields[0].All(Uri.IsHexDigit)
                && string.Equals(fields[1].TrimStart('*'), ExecutableAssetName, StringComparison.OrdinalIgnoreCase))
            {
                return fields[0].ToLowerInvariant();
            }
        }

        throw new InvalidDataException($"{ChecksumAssetName} does not contain a hash for {ExecutableAssetName}.");
    }

    private async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
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
