using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sigstore;

namespace CodexUsageTray.Tests;

public sealed class GitHubReleaseAttestationVerifierTests
{
    private const string ReleaseRef = "31c6d79ff6629e950620a21a8483030eed9b54e9";
    private const string ExecutableHash = "a960b6c44804e736efc73d9adba0f7e64f9caa47da9d6237d300e8c6f9c4ce32";

    [Fact]
    public async Task AcceptsRealGitHubReleaseAttestation()
    {
        using var client = CreateClient(ReleaseRef, ReadFixture("ReleaseAttestation-v2.2.3.json"));
        var verifier = CreateVerifier(client);

        await verifier.VerifyAsync(new Version(2, 2, 3), ExecutableHash, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("2.2.4", ExecutableHash, ReleaseRef)]
    [InlineData("2.2.3.1", ExecutableHash, ReleaseRef)]
    [InlineData("2.2.3", "b960b6c44804e736efc73d9adba0f7e64f9caa47da9d6237d300e8c6f9c4ce32", ReleaseRef)]
    [InlineData("2.2.3", ExecutableHash, "41c6d79ff6629e950620a21a8483030eed9b54e9")]
    public async Task RejectsWrongTagExecutableOrGitRef(string version, string hash, string releaseRef)
    {
        using var client = CreateClient(releaseRef, ReadFixture("ReleaseAttestation-v2.2.3.json"));
        var verifier = CreateVerifier(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            verifier.VerifyAsync(Version.Parse(version), hash, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsAlteredAttestationSignature()
    {
        var bundle = JsonNode.Parse(ReadFixture("ReleaseAttestation-v2.2.3.json"))!;
        bundle["dsseEnvelope"]!["signatures"]![0]!["sig"] = Convert.ToBase64String(new byte[64]);
        using var client = CreateClient(ReleaseRef, bundle.ToJsonString());
        var verifier = CreateVerifier(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            verifier.VerifyAsync(new Version(2, 2, 3), ExecutableHash, TestContext.Current.CancellationToken));
    }

    private static GitHubReleaseAttestationVerifier CreateVerifier(HttpClient client)
    {
        var root = GitHubTrustRootProvider.ParseTrustedRoot(
            Encoding.UTF8.GetBytes(ReadFixture("GitHubTrustedRoot.json")));
        return new GitHubReleaseAttestationVerifier(client, new InMemoryTrustRootProvider(root));
    }

    private static HttpClient CreateClient(string releaseRef, string bundle)
    {
        var attestation = JsonSerializer.Serialize(new
        {
            attestations = new[]
            {
                new
                {
                    repository_id = 1360347795L,
                    initiator = "github",
                    bundle = JsonSerializer.Deserialize<JsonElement>(bundle)
                }
            }
        });
        return new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = path.Contains("/git/ref/tags/", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { @object = new { sha = releaseRef } })
                : path.Contains("/attestations/", StringComparison.Ordinal)
                    ? attestation
                    : throw new InvalidOperationException($"Unexpected URL: {request.RequestUri}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
    }

    private static string ReadFixture(string name)
    {
        using var stream = typeof(GitHubReleaseAttestationVerifierTests).Assembly.GetManifestResourceStream(
            $"CodexUsageTray.Tests.Fixtures.{name}")
            ?? throw new InvalidOperationException($"Missing fixture: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
