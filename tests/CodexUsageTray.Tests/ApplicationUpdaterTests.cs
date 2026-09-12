using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class ApplicationUpdaterTests
{
    [Fact]
    public async Task NewReleaseIsDownloadedToStagingDirectoryAndVerified()
    {
        var directory = CreateTestDirectory();
        var payload = Encoding.UTF8.GetBytes("replacement executable");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = JsonResponse(CreateRelease("v1.3.0")),
            ["/downloads/SHA256SUMS.txt"] = TextResponse($"{expectedHash}  CodexUsageTray.exe\n"),
            ["/downloads/CodexUsageTray.exe"] = ByteResponse(payload)
        });
        var updater = new ApplicationUpdater(
            httpClient,
            new Version(1, 2, 0),
            directory);
        ApplicationUpdate? update = null;

        try
        {
            var availableUpdate = await updater.CheckAsync(CancellationToken.None);
            Assert.NotNull(availableUpdate);
            update = await updater.DownloadAsync(availableUpdate, CancellationToken.None);

            Assert.NotNull(update);
            Assert.Equal(new Version(1, 3, 0), update.Version);
            Assert.Equal(directory, Path.GetDirectoryName(update.StagedPath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(update.StagedPath, CancellationToken.None));
        }
        finally
        {
            if (update is not null)
            {
                ApplicationUpdater.TryDelete(update.StagedPath);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentReleaseDoesNotDownloadAssets()
    {
        var directory = CreateTestDirectory();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return request.RequestUri?.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) == true
                ? JsonResponse(CreateRelease("v1.2.0", includeAssets: false))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updater = new ApplicationUpdater(
            httpClient,
            new Version(1, 2, 0),
            directory);

        try
        {
            var update = await updater.CheckAsync(CancellationToken.None);

            Assert.Null(update);
            Assert.Equal(1, requestCount);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidDownloadIsDeletedWhenChecksumDoesNotMatch()
    {
        var directory = CreateTestDirectory();
        var payload = Encoding.UTF8.GetBytes("invalid replacement");
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = JsonResponse(CreateRelease("v2.0.0")),
            ["/downloads/SHA256SUMS.txt"] = TextResponse($"{new string('0', 64)}  CodexUsageTray.exe\n"),
            ["/downloads/CodexUsageTray.exe"] = ByteResponse(payload)
        });
        var updater = new ApplicationUpdater(
            httpClient,
            new Version(1, 2, 0),
            directory);

        try
        {
            var availableUpdate = await updater.CheckAsync(CancellationToken.None);
            Assert.NotNull(availableUpdate);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => updater.DownloadAsync(availableUpdate, CancellationToken.None));

            Assert.Contains("failed its SHA-256 check", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewReleaseCheckDoesNotDownloadAssets()
    {
        var directory = CreateTestDirectory();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return request.RequestUri?.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) == true
                ? JsonResponse(CreateRelease("v1.3.0"))
                : throw new InvalidOperationException("The update asset was downloaded during the release check.");
        }));
        var updater = new ApplicationUpdater(
            httpClient,
            new Version(1, 2, 0),
            directory);

        try
        {
            var update = await updater.CheckAsync(CancellationToken.None);

            Assert.NotNull(update);
            Assert.Equal(new Version(1, 3, 0), update.Version);
            Assert.Equal(1, requestCount);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static string CreateRelease(string tagName, bool includeAssets = true)
    {
        object[] assets = includeAssets
            ?
            [
                new
                {
                    name = "CodexUsageTray.exe",
                    browser_download_url = "https://example.test/downloads/CodexUsageTray.exe"
                },
                new
                {
                    name = "SHA256SUMS.txt",
                    browser_download_url = "https://example.test/downloads/SHA256SUMS.txt"
                }
            ]
            : [];
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

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CodexUsageTray-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handleRequest)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handleRequest(request));
    }
}
