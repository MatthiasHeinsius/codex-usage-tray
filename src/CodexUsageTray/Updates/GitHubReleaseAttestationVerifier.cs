using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sigstore;
using Tuf;

namespace CodexUsageTray;

internal sealed class GitHubReleaseAttestationVerifier
{
    private const string Repository = "MatthiasHeinsius/codex-usage-tray";
    private const string RepositoryId = "1360347795";
    private const string ExecutableName = "CodexUsageTray.exe";
    private const string ReleasePredicateType = "https://in-toto.io/attestation/release/v0.2";
    private const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly ITrustRootProvider trustRootProvider;

    internal GitHubReleaseAttestationVerifier(HttpClient httpClient, ITrustRootProvider? trustRootProvider = null)
    {
        this.httpClient = httpClient;
        this.trustRootProvider = trustRootProvider ?? new GitHubTrustRootProvider();
    }

    internal async Task VerifyAsync(Version version, string executableHash, CancellationToken cancellationToken)
    {
        if (version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException("The release version must have exactly three components.");
        }

        var tag = $"v{version.ToString(3)}";
        var refUri = new Uri($"https://api.github.com/repos/{Repository}/git/ref/tags/{tag}");
        var refBytes = await GitHubApplicationUpdateSource.DownloadLimitedBytesAsync(
            httpClient, refUri, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        var releaseRef = JsonSerializer.Deserialize<GitRef>(refBytes)?.Object.Sha
            ?? throw new InvalidDataException("The release tag has no Git object ID.");
        var algorithm = releaseRef.Length switch
        {
            40 => "sha1",
            64 => "sha256",
            _ => throw new InvalidDataException("The release tag has an invalid Git object ID.")
        };
        if (!releaseRef.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The release tag has an invalid Git object ID.");
        }

        var attestationsUri = new Uri(
            $"https://api.github.com/repos/{Repository}/attestations/{algorithm}:{releaseRef}?predicate_type=release&per_page=100");
        var attestationBytes = await GitHubApplicationUpdateSource.DownloadLimitedBytesAsync(
            httpClient, attestationsUri, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<AttestationResponse>(attestationBytes)
            ?? throw new InvalidDataException("GitHub returned no release attestations.");
        var verifier = new SigstoreVerifier(trustRootProvider);
        var executableDigest = Convert.FromHexString(executableHash);

        foreach (var attestation in response.Attestations)
        {
            if (attestation.Initiator != "github" || attestation.RepositoryId != 1360347795)
            {
                continue;
            }

            try
            {
                var bundle = SigstoreBundle.Deserialize(attestation.Bundle.GetRawText());
                var result = await verifier.VerifyDigestAsync(
                    executableDigest,
                    HashAlgorithmType.Sha256,
                    bundle,
                    new VerificationPolicy
                    {
                        CertificateIdentity = new CertificateIdentity
                        {
                            SubjectAlternativeName = "https://dotcom.releases.github.com"
                        },
                        RequireTransparencyLog = false
                    },
                    cancellationToken).ConfigureAwait(false);
                // Sigstore verifies the signed Release statement, but its digest argument
                // does not bind this asset because the Git ref is the statement's first subject.
                if (MatchesRelease(result, tag, releaseRef, algorithm, executableHash))
                {
                    return;
                }
            }
            catch (VerificationException)
            {
                // Another attestation for this commit may not be the requested release.
            }
        }

        throw new InvalidDataException("The update executable does not match a verified GitHub Release attestation.");
    }

    private static bool MatchesRelease(
        VerificationResult result,
        string tag,
        string releaseRef,
        string refAlgorithm,
        string executableHash)
    {
        var statement = result.Statement;
        if (statement?.PredicateType != ReleasePredicateType
            || result.VerifiedTimestamps.Count == 0
            || statement.Predicate is not { } predicate
            || !predicate.TryGetProperty("repository", out var repository)
            || repository.GetString() != Repository
            || !predicate.TryGetProperty("repositoryId", out var repositoryId)
            || repositoryId.GetString() != RepositoryId
            || !predicate.TryGetProperty("tag", out var signedTag)
            || signedTag.GetString() != tag
            || !predicate.TryGetProperty("purl", out var purl)
            || purl.GetString() != $"pkg:github/{Repository}@{tag}")
        {
            return false;
        }

        return statement.Subject.Any(subject =>
                   subject.Digest.TryGetValue(refAlgorithm, out var digest)
                   && string.Equals(digest, releaseRef, StringComparison.OrdinalIgnoreCase))
            && statement.Subject.Any(subject =>
                subject.Name == ExecutableName
                && subject.Digest.TryGetValue("sha256", out var digest)
                && string.Equals(digest, executableHash, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record GitRef([property: JsonPropertyName("object")] GitObject Object);
    private sealed record GitObject([property: JsonPropertyName("sha")] string Sha);
    private sealed record AttestationResponse(
        [property: JsonPropertyName("attestations")] Attestation[] Attestations);
    private sealed record Attestation(
        [property: JsonPropertyName("repository_id")] long RepositoryId,
        [property: JsonPropertyName("initiator")] string Initiator,
        [property: JsonPropertyName("bundle")] JsonElement Bundle);
}

internal sealed class GitHubTrustRootProvider : ITrustRootProvider
{
    private static readonly Uri TufRepository = new("https://tuf-repo.github.com/");

    public async Task<TrustedRoot> GetTrustRootAsync(CancellationToken cancellationToken = default)
    {
        using var stream = typeof(GitHubTrustRootProvider).Assembly.GetManifestResourceStream(
            "CodexUsageTray.GitHubTufRoot.json")
            ?? throw new InvalidOperationException("The GitHub TUF bootstrap root is unavailable.");
        using var bootstrap = new MemoryStream();
        await stream.CopyToAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        using var tuf = new TufClient(new TufClientOptions
        {
            MetadataBaseUrl = TufRepository,
            TargetsBaseUrl = new Uri(TufRepository, "targets/"),
            TrustedRoot = bootstrap.ToArray()
        });
        var rootBytes = await tuf.DownloadTargetAsync("trusted_root.json", cancellationToken)
            .ConfigureAwait(false);
        return ParseTrustedRoot(rootBytes);
    }

    internal static TrustedRoot ParseTrustedRoot(byte[] rootBytes)
    {
        var root = JsonNode.Parse(rootBytes)
            ?? throw new InvalidDataException("GitHub returned an empty trusted root.");

        // GitHub publishes hostname-only URI fields; Sigstore 0.5 expects absolute URIs.
        foreach (var field in new[] { "certificateAuthorities", "timestampAuthorities" })
        {
            foreach (var authority in root[field]?.AsArray() ?? [])
            {
                var uri = (string?)authority?["uri"];
                if (uri is not null && !uri.Contains("://", StringComparison.Ordinal))
                {
                    authority!["uri"] = "https://" + uri;
                }
            }
        }

        return TrustedRoot.Deserialize(root.ToJsonString());
    }
}
