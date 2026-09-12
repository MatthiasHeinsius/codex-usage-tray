using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageTray;

internal sealed class ApplicationUpdater
{
    private const string ExecutableAssetName = "CodexUsageTray.exe";
    private const string ChecksumAssetName = "SHA256SUMS.txt";
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/MatthiasHeinsius/codex-usage-tray/releases/latest");
    private readonly HttpClient httpClient;
    private readonly Version currentVersion;
    private readonly string updateDirectory;

    internal ApplicationUpdater(HttpClient httpClient, Version currentVersion, string updateDirectory)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(updateDirectory);
        this.httpClient = httpClient;
        this.currentVersion = currentVersion;
        this.updateDirectory = Path.GetFullPath(updateDirectory);
    }

    internal Version CurrentVersion => currentVersion;

    internal static ApplicationUpdater CreateDefault()
    {
        var version = typeof(ApplicationUpdater).Assembly.GetName().Version
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
        return new ApplicationUpdater(client, version, Path.GetTempPath());
    }

    internal async Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var releaseResponse = await httpClient.GetAsync(LatestReleaseUri, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            releaseStream,
            cancellationToken: cancellationToken)
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

    internal async Task<ApplicationUpdate> DownloadAsync(
        AvailableApplicationUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var expectedHash = await DownloadExpectedHashAsync(update.ChecksumDownloadUrl, cancellationToken);
        var stagedPath = CreateStagedPath(update.Version);
        try
        {
            await DownloadFileAsync(update.ExecutableDownloadUrl, stagedPath, cancellationToken);
            var actualHash = await ComputeSha256Async(stagedPath, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The downloaded update failed its SHA-256 check. Expected {expectedHash}, got {actualHash}.");
            }

            return new ApplicationUpdate(update.Version, stagedPath);
        }
        catch
        {
            TryDelete(stagedPath);
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
        var contents = await httpClient.GetStringAsync(uri, cancellationToken);
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
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken);
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
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private string CreateStagedPath(Version version)
    {
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"CodexUsageTray-{version}-update-{Guid.NewGuid():N}.tmp");
        return Path.Combine(updateDirectory, fileName);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri DownloadUrl);
}

internal sealed record AvailableApplicationUpdate(
    Version Version,
    Uri ExecutableDownloadUrl,
    Uri ChecksumDownloadUrl);

internal sealed record ApplicationUpdate(Version Version, string StagedPath);
