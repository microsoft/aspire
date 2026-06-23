// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Npm;
using Microsoft.Extensions.Logging;
using Sigstore;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Verifies GitHub release asset attestations for downloaded Aspire skills artifacts.
/// </summary>
internal interface IGitHubArtifactAttestationVerifier
{
    Task<ProvenanceVerificationResult> VerifyAsync(
        string repository,
        string artifactPath,
        string expectedSourceRepository,
        string expectedWorkflowPath,
        string expectedBuildType,
        string expectedVersion,
        CancellationToken cancellationToken);
}

internal sealed class GitHubArtifactAttestationVerifier(HttpClient httpClient, ILogger<GitHubArtifactAttestationVerifier> logger) : IGitHubArtifactAttestationVerifier
{
    private const string GitHubApiBaseUrl = "https://api.github.com";

    public async Task<ProvenanceVerificationResult> VerifyAsync(
        string repository,
        string artifactPath,
        string expectedSourceRepository,
        string expectedWorkflowPath,
        string expectedBuildType,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var digestBytes = ComputeSha256(artifactPath);
        var digestHex = Convert.ToHexString(digestBytes).ToLowerInvariant();
        var attestationUrl = $"{GitHubApiBaseUrl}/repos/{repository}/attestations/sha256:{digestHex}";

        string attestationJson;
        try
        {
            using var request = CreateGitHubRequest(attestationUrl);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Failed to fetch GitHub artifact attestations for {Repository} digest {Digest}: HTTP {StatusCode}", repository, digestHex, response.StatusCode);
                return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationFetchFailed };
            }

            attestationJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Failed to fetch GitHub artifact attestations for {Repository} digest {Digest}.", repository, digestHex);
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationFetchFailed };
        }

        var bundleJson = ExtractBundleJson(attestationJson, out var parseFailed);
        if (bundleJson is null)
        {
            return new ProvenanceVerificationResult
            {
                Outcome = parseFailed
                    ? ProvenanceVerificationOutcome.AttestationParseFailed
                    : ProvenanceVerificationOutcome.SlsaProvenanceNotFound
            };
        }

        SigstoreBundle bundle;
        try
        {
            bundle = SigstoreBundle.Deserialize(bundleJson);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to deserialize GitHub artifact attestation bundle for {Repository} digest {Digest}.", repository, digestHex);
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed };
        }

        if (!SigstoreNpmProvenanceChecker.TryParseGitHubOwnerRepo(expectedSourceRepository, out var owner, out var repo))
        {
            logger.LogWarning("Could not parse GitHub owner/repo from expected source repository: {ExpectedSourceRepository}", expectedSourceRepository);
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.SourceRepositoryMismatch };
        }

        try
        {
            var verifier = new SigstoreVerifier();
            var policy = new VerificationPolicy
            {
                CertificateIdentity = CertificateIdentity.ForGitHubActions(owner, repo)
            };

            var (success, result) = await verifier.TryVerifyDigestAsync(
                digestBytes,
                HashAlgorithmType.Sha256,
                bundle,
                policy,
                cancellationToken).ConfigureAwait(false);

            if (!success || result is null)
            {
                logger.LogWarning("Sigstore verification failed for GitHub artifact {ArtifactPath}: {FailureReason}", artifactPath, result?.FailureReason);
                return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed };
            }

            var provenance = SigstoreNpmProvenanceChecker.ExtractProvenanceFromResult(result);
            if (provenance is null)
            {
                logger.LogDebug("Failed to extract provenance data from GitHub artifact attestation for {ArtifactPath}.", artifactPath);
                return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed };
            }

            return SigstoreNpmProvenanceChecker.VerifyProvenanceFields(
                provenance,
                expectedSourceRepository,
                expectedWorkflowPath,
                expectedBuildType,
                refInfo => IsExpectedTagRef(refInfo, expectedVersion));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sigstore verification threw an exception for GitHub artifact {ArtifactPath}.", artifactPath);
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed };
        }
    }

    private static HttpRequestMessage CreateGitHubRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("aspire-cli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static string? ExtractBundleJson(string attestationJson, out bool parseFailed)
    {
        var bundleJson = SigstoreNpmProvenanceChecker.ExtractSlsaBundleJson(attestationJson, out parseFailed);
        if (bundleJson is not null || parseFailed)
        {
            return bundleJson;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(attestationJson);
        }
        catch (JsonException)
        {
            parseFailed = true;
            return null;
        }

        if (document?["attestations"] is not JsonArray attestations)
        {
            return null;
        }

        foreach (var attestation in attestations)
        {
            var bundleNode = attestation?["bundle"];
            if (bundleNode is null)
            {
                continue;
            }

            if (bundleNode is JsonValue bundleValue && bundleValue.TryGetValue<string>(out var bundleString))
            {
                return bundleString;
            }

            return bundleNode.ToJsonString();
        }

        return null;
    }

    private static byte[] ComputeSha256(string artifactPath)
    {
        using var stream = File.OpenRead(artifactPath);
        return SHA256.HashData(stream);
    }

    private static bool IsExpectedTagRef(WorkflowRefInfo refInfo, string version)
    {
        return string.Equals(refInfo.Kind, "tags", StringComparison.Ordinal) &&
               GetGitHubTagCandidates(version).Any(tag => string.Equals(refInfo.Name, tag, StringComparison.Ordinal));
    }

    private static IEnumerable<string> GetGitHubTagCandidates(string version)
    {
        if (version.StartsWith('v') || version.StartsWith('V'))
        {
            yield return version;
            yield return version[1..];
            yield break;
        }

        yield return $"v{version}";
        yield return version;
    }
}
