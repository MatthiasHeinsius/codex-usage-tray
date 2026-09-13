using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class GitHubApplicationUpdateSourceTests
{
    [Theory]
    [InlineData("{0}  CodexUsageTray.exe")]
    [InlineData("{0} *CODEXUSAGETRAY.EXE")]
    public async Task DownloadStagesExecutableWhenChecksumManifestMatches(string checksumLine)
    {
        using var directory = new TemporaryDirectory("update-download");
        var payload = Encoding.UTF8.GetBytes("replacement executable");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = JsonResponse(CreateRelease("v1.3.0")),
            ["/downloads/SHA256SUMS.txt"] = TextResponse(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, checksumLine, expectedHash)),
            ["/downloads/CodexUsageTray.exe"] = ByteResponse(payload)
        });
        var source = new GitHubApplicationUpdateSource(
            httpClient,
            new Version(1, 2, 0),
            directory.RootPath);

        var availableUpdate = await source.CheckAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(availableUpdate);
        var update = await source.DownloadAsync(
            availableUpdate,
            TestContext.Current.CancellationToken);

        Assert.Equal(new Version(1, 3, 0), update.Version);
        Assert.Equal(directory.RootPath, Path.GetDirectoryName(update.StagedPath));
        Assert.Equal(
            payload,
            await File.ReadAllBytesAsync(update.StagedPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckReturnsNullForCurrentReleaseWithoutRequiringAssets()
    {
        using var directory = new TemporaryDirectory("current-update");
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return request.RequestUri?.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) == true
                ? JsonResponse(CreateRelease("v1.2.0", includeExecutable: false, includeChecksum: false))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var source = new GitHubApplicationUpdateSource(
            httpClient,
            new Version(1, 2, 0),
            directory.RootPath);

        var update = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(update);
        Assert.Equal(1, requestCount);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    [Fact]
    public async Task DownloadDeletesStagedFileWhenChecksumDoesNotMatch()
    {
        using var directory = new TemporaryDirectory("invalid-update");
        var payload = Encoding.UTF8.GetBytes("invalid replacement");
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = JsonResponse(CreateRelease("v2.0.0")),
            ["/downloads/SHA256SUMS.txt"] = TextResponse($"{new string('0', 64)}  CodexUsageTray.exe\n"),
            ["/downloads/CodexUsageTray.exe"] = ByteResponse(payload)
        });
        var source = new GitHubApplicationUpdateSource(
            httpClient,
            new Version(1, 2, 0),
            directory.RootPath);

        var availableUpdate = await source.CheckAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(availableUpdate);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAsync(availableUpdate, TestContext.Current.CancellationToken));

        Assert.Contains("staged executable failed its SHA-256 check", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    [Fact]
    public async Task CheckReturnsMetadataWithoutDownloadingAssets()
    {
        using var directory = new TemporaryDirectory("update-check");
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return request.RequestUri?.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) == true
                ? JsonResponse(CreateRelease("v1.3.0"))
                : throw new InvalidOperationException("The update asset was downloaded during the release check.");
        }));
        var source = new GitHubApplicationUpdateSource(
            httpClient,
            new Version(1, 2, 0),
            directory.RootPath);

        var update = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 0), update.Version);
        Assert.Equal(1, requestCount);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    [Theory]
    [InlineData("release", true, true, "not a valid version")]
    [InlineData("v1.3.0", false, true, "does not contain CodexUsageTray.exe")]
    [InlineData("v1.3.0", true, false, "does not contain SHA256SUMS.txt")]
    public async Task CheckRejectsMalformedReleaseMetadata(
        string tag,
        bool includeExecutable,
        bool includeChecksum,
        string expectedMessage)
    {
        using var directory = new TemporaryDirectory("malformed-release");
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] =
                JsonResponse(CreateRelease(tag, includeExecutable, includeChecksum))
        });
        var source = new GitHubApplicationUpdateSource(httpClient, new Version(1, 2, 0), directory.RootPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => source.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-sha256  CodexUsageTray.exe")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg  CodexUsageTray.exe")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000  another.exe")]
    public async Task DownloadRejectsChecksumManifestWithoutAValidExecutableHash(string manifest)
    {
        using var directory = new TemporaryDirectory("missing-checksum");
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = JsonResponse(CreateRelease("v1.3.0")),
            ["/downloads/SHA256SUMS.txt"] = TextResponse(manifest)
        });
        var source = new GitHubApplicationUpdateSource(httpClient, new Version(1, 2, 0), directory.RootPath);
        var availableUpdate = await source.CheckAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(availableUpdate);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => source.DownloadAsync(availableUpdate, TestContext.Current.CancellationToken));

        Assert.Equal(
            "SHA256SUMS.txt does not contain a hash for CodexUsageTray.exe.",
            exception.Message);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    private static HttpClient CreateHttpClient(Dictionary<string, HttpResponseMessage> responses) =>
        new(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("The test request URI is unavailable.");
            return responses.TryGetValue(path, out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

    private static string CreateRelease(
        string tagName,
        bool includeExecutable = true,
        bool includeChecksum = true)
    {
        var assets = new List<object>();
        if (includeExecutable)
        {
            assets.Add(new
            {
                name = "CodexUsageTray.exe",
                browser_download_url = "https://example.test/downloads/CodexUsageTray.exe"
            });
        }

        if (includeChecksum)
        {
            assets.Add(new
            {
                name = "SHA256SUMS.txt",
                browser_download_url = "https://example.test/downloads/SHA256SUMS.txt"
            });
        }

        return JsonSerializer.Serialize(new
        {
            tag_name = tagName,
            assets
        });
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.ASCII, "text/plain")
    };

    private static HttpResponseMessage ByteResponse(byte[] contents) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(contents)
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handleRequest)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handleRequest(request));
    }
}
