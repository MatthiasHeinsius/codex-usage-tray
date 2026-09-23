using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray.Tests;

public sealed class GitHubApplicationUpdateSourceTests
{
    [Fact]
    public async Task DownloadStagesExecutableOnlyAfterReleaseVerification()
    {
        using var directory = new TemporaryDirectory("update-download");
        var payload = Encoding.UTF8.GetBytes("replacement executable");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = JsonResponse(CreateRelease("v1.3.0")),
            ["/downloads/CodexUsageTray.exe"] = ByteResponse(payload)
        });
        Version? verifiedVersion = null;
        string? verifiedHash = null;
        var source = CreateSource(client, new Version(1, 2, 0), directory.RootPath,
            (version, hash, _) =>
            {
                verifiedVersion = version;
                verifiedHash = hash;
                return Task.CompletedTask;
            });

        var available = await source.CheckAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(available);
        using var staged = await source.DownloadAsync(available, TestContext.Current.CancellationToken);

        Assert.Equal(new Version(1, 3, 0), verifiedVersion);
        Assert.Equal(expectedHash, verifiedHash);
        Assert.Equal(expectedHash, staged.ExpectedHash);
        Assert.Equal(payload, await File.ReadAllBytesAsync(staged.StagedPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadDeletesStagedFileWhenReleaseVerificationFails()
    {
        using var directory = new TemporaryDirectory("invalid-update");
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/downloads/CodexUsageTray.exe"] = ByteResponse([1, 2, 3])
        });
        var source = CreateSource(client, new Version(1, 0, 0), directory.RootPath,
            (_, _, _) => throw new InvalidDataException("Release attestation did not match."));
        var update = new AvailableApplicationUpdate(
            new Version(2, 0, 0), new Uri("https://example.test/downloads/CodexUsageTray.exe"));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.DownloadAsync(update, TestContext.Current.CancellationToken));

        Assert.Contains("attestation did not match", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    [Fact]
    public async Task CheckReturnsNullForCurrentReleaseWithoutRequiringAssets()
    {
        using var directory = new TemporaryDirectory("current-update");
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] =
                JsonResponse(CreateRelease("v1.2.0", includeExecutable: false))
        });
        var source = CreateSource(client, new Version(1, 2, 0), directory.RootPath);

        Assert.Null(await source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    [Fact]
    public async Task CheckReturnsMetadataWithoutDownloadingExecutable()
    {
        using var directory = new TemporaryDirectory("update-check");
        var requests = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests++;
            return request.RequestUri?.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) == true
                ? JsonResponse(CreateRelease("v1.3.0"))
                : throw new InvalidOperationException("The executable was downloaded during the release check.");
        }));
        var source = CreateSource(client, new Version(1, 2, 0), directory.RootPath);

        var update = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 0), update.Version);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledReleaseCheckStopsOnTimeoutOrCancellation(bool cancelCaller)
    {
        using var directory = new TemporaryDirectory("stalled-update-check");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var body = new StalledDownloadStream();
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            }
        });
        client.Timeout = cancelCaller ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1);
        var source = CreateSource(client, new Version(1, 0, 0), directory.RootPath);
        var check = source.CheckAsync(cancellation.Token);
        try
        {
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<TimeoutException>(
                    () => check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.Equal("The update request timed out. Try again.", failure.Message);
            }
        }
        finally
        {
            cancellation.Cancel();
            body.CancelPendingRead();
            try
            {
                await check;
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
            }
        }
    }

    [Theory]
    [InlineData("release", true, "not a valid version")]
    [InlineData("v1.3.0.1", true, "not a valid version")]
    [InlineData("v01.3.0", true, "not a valid version")]
    [InlineData("1.3.0", true, "not a valid version")]
    [InlineData("v1.3.0", false, "does not contain CodexUsageTray.exe")]
    public async Task CheckRejectsMalformedReleaseMetadata(
        string tag, bool includeExecutable, string expectedMessage)
    {
        using var directory = new TemporaryDirectory("malformed-release");
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/repos/MatthiasHeinsius/codex-usage-tray/releases/latest"] =
                JsonResponse(CreateRelease(tag, includeExecutable))
        });
        var source = CreateSource(client, new Version(1, 2, 0), directory.RootPath);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRejectsOversizedExecutableBeforeWritingIt()
    {
        using var directory = new TemporaryDirectory("oversized-update");
        var response = ByteResponse([1]);
        response.Content.Headers.ContentLength = 250L * 1024 * 1024 + 1;
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/downloads/CodexUsageTray.exe"] = response
        });
        var source = CreateSource(client, new Version(1, 0, 0), directory.RootPath);
        var update = new AvailableApplicationUpdate(
            new Version(2, 0, 0), new Uri("https://example.test/downloads/CodexUsageTray.exe"));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.DownloadAsync(update, TestContext.Current.CancellationToken));

        Assert.Contains("exceeds its size limit", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledDownloadStopsAndDeletesPartialFile(bool cancelCaller)
    {
        using var directory = new TemporaryDirectory("stalled-update");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var body = new StalledDownloadStream();
        using var client = CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["/downloads/CodexUsageTray.exe"] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            }
        });
        client.Timeout = cancelCaller ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1);
        var source = CreateSource(client, new Version(1, 0, 0), directory.RootPath);
        var update = new AvailableApplicationUpdate(
            new Version(2, 0, 0), new Uri("https://example.test/downloads/CodexUsageTray.exe"));
        var download = source.DownloadAsync(update, cancellation.Token);
        try
        {
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (cancelCaller)
            {
                Assert.Single(Directory.EnumerateFiles(directory.RootPath));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => download.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<TimeoutException>(
                    () => download.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.Equal("The update download timed out. Try again.", failure.Message);
            }

            Assert.Empty(Directory.EnumerateFiles(directory.RootPath));
        }
        finally
        {
            cancellation.Cancel();
            body.CancelPendingRead();
            try
            {
                using var unused = await download;
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
            }
        }
    }

    private static GitHubApplicationUpdateSource CreateSource(
        HttpClient client,
        Version version,
        string directory,
        Func<Version, string, CancellationToken, Task>? verify = null) =>
        new(client, version, directory, verify ?? ((_, _, _) => Task.CompletedTask));

    private static HttpClient CreateHttpClient(Dictionary<string, HttpResponseMessage> responses) =>
        new(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("The test request URI is unavailable.");
            return responses.TryGetValue(path, out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

    private static string CreateRelease(string tag, bool includeExecutable = true)
    {
        var assets = includeExecutable
            ? new[] { new { name = "CodexUsageTray.exe", browser_download_url = "https://example.test/downloads/CodexUsageTray.exe" } }
            : [];
        return JsonSerializer.Serialize(new { tag_name = tag, assets });
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
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

    private sealed class StalledDownloadStream : Stream
    {
        private bool sentPrefix;
        private readonly TaskCompletionSource pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void CancelPendingRead() => pendingRead.TrySetCanceled();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sentPrefix)
            {
                sentPrefix = true;
                buffer.Span[0] = 1;
                return 1;
            }

            Waiting.TrySetResult();
            await pendingRead.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
