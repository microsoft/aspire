// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sigstore;

namespace Aspire.Cli.Npm;

/// <summary>
/// Verifies npm package provenance by cryptographically verifying Sigstore bundles
/// from the npm registry attestations API using the Sigstore .NET library.
/// Uses Fulcio certificate extensions and in-toto statement APIs for attestation analysis.
/// </summary>
internal sealed class SigstoreNpmProvenanceChecker(HttpClient httpClient, ILogger<SigstoreNpmProvenanceChecker> logger) : INpmProvenanceChecker
{
    internal const string NpmRegistryAttestationsBaseUrl = "https://registry.npmjs.org/-/npm/v1/attestations";
    internal const string SlsaProvenancePredicateType = "https://slsa.dev/provenance/v1";

    /// <inheritdoc />
    public async Task<ProvenanceVerificationResult> VerifyProvenanceAsync(
        string packageName,
        string version,
        string expectedSourceRepository,
        string expectedWorkflowPath,
        string expectedBuildType,
        Func<WorkflowRefInfo, bool>? validateWorkflowRef,
        CancellationToken cancellationToken,
        string? sriIntegrity = null)
    {
        var json = await FetchAttestationJsonAsync(packageName, version, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationFetchFailed };
        }

        // Extract the SLSA provenance bundle JSON from the npm attestation response.
        var bundleJson = ExtractSlsaBundleJson(json, out var parseFailed);
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
            logger.LogDebug(ex, "Failed to deserialize Sigstore bundle for {Package}@{Version}", packageName, version);
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed };
        }

        // Verify the bundle with a policy that uses CertificateIdentity.ForGitHubActions
        // to check the SAN (Subject Alternative Name) and issuer in the Fulcio certificate.
        // This verifies the signing identity originates from the expected GitHub repository.
        var (sigstoreFailure, verificationResult) = await VerifySigstoreBundleAsync(
            bundle, expectedSourceRepository, sriIntegrity,
            packageName, version, cancellationToken).ConfigureAwait(false);
        if (sigstoreFailure is not null)
        {
            return sigstoreFailure;
        }

        // Extract provenance from the verified result's in-toto statement and certificate extensions.
        var provenance = ExtractProvenanceFromResult(verificationResult!);
        if (provenance is null)
        {
            return new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed };
        }

        return VerifyProvenanceFields(
            provenance, expectedSourceRepository, expectedWorkflowPath,
            expectedBuildType, validateWorkflowRef);
    }

    /// <summary>
    /// Fetches the attestation JSON from the npm registry for the given package and version.
    /// </summary>
    private async Task<string?> FetchAttestationJsonAsync(
        string packageName, string version, CancellationToken cancellationToken)
    {
        try
        {
            var encodedPackage = Uri.EscapeDataString(packageName);
            var url = $"{NpmRegistryAttestationsBaseUrl}/{encodedPackage}@{version}";

            logger.LogDebug("Fetching attestations from {Url}", url);
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Failed to fetch attestations: HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Failed to fetch attestations for {Package}@{Version}", packageName, version);
            return null;
        }
    }

    /// <summary>
    /// Extracts the Sigstore bundle JSON string for the SLSA provenance attestation
    /// from the npm registry attestations API response.
    /// Returns the bundle JSON on success, or <c>null</c> if the JSON is malformed or
    /// no SLSA provenance attestation is found.
    /// </summary>
    /// <param name="attestationJson">The raw JSON from the npm attestations API.</param>
    /// <param name="parseFailed">Set to <c>true</c> when the input is not valid JSON; <c>false</c> otherwise.</param>
    internal static string? ExtractSlsaBundleJson(string attestationJson, out bool parseFailed)
    {
        parseFailed = false;
        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(attestationJson);
        }
        catch (JsonException)
        {
            parseFailed = true;
            return null;
        }

        var attestationsNode = doc?["attestations"];
        if (attestationsNode is not JsonArray { Count: >0 } attestations)
        {
            return null;
        }

        foreach (var attestation in attestations)
        {
            if (attestation is not JsonObject attestationObj)
            {
                continue;
            }

            var predicateTypeNode = attestationObj["predicateType"];
            if (predicateTypeNode is not JsonValue predicateTypeValue)
            {
                continue;
            }

            string? predicateType;
            try
            {
                predicateType = predicateTypeValue.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!string.Equals(predicateType, SlsaProvenancePredicateType, StringComparison.Ordinal))
            {
                continue;
            }

            var bundleNode = attestationObj["bundle"];
            return bundleNode?.ToJsonString();
        }

        return null;
    }

    /// <summary>
    /// Cryptographically verifies the Sigstore bundle using the Sigstore library.
    /// Checks the Fulcio certificate chain, Rekor transparency log inclusion, OIDC identity,
    /// and source repository via CertificateExtensionPolicy.
    /// </summary>
    /// <returns>A failure result and null verification result on error; null failure and the verification result on success.</returns>
    private async Task<(ProvenanceVerificationResult? Failure, VerificationResult? Result)> VerifySigstoreBundleAsync(
        SigstoreBundle bundle,
        string expectedSourceRepository,
        string? sriIntegrity,
        string packageName,
        string version,
        CancellationToken cancellationToken)
    {
        if (!TryParseGitHubOwnerRepo(expectedSourceRepository, out var owner, out var repo))
        {
            logger.LogWarning("Could not parse GitHub owner/repo from expected source repository: {ExpectedSourceRepository}", expectedSourceRepository);
            return (new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.SourceRepositoryMismatch }, null);
        }

        var verifier = new SigstoreVerifier();
        var policy = new VerificationPolicy
        {
            CertificateIdentity = CertificateIdentity.ForGitHubActions(owner, repo)
        };

        try
        {
            bool success;
            VerificationResult? result;

            if (sriIntegrity is not null && sriIntegrity.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase))
            {
                var hashBase64 = sriIntegrity["sha512-".Length..];
                var digestBytes = Convert.FromBase64String(hashBase64);

                (success, result) = await verifier.TryVerifyDigestAsync(
                    digestBytes, HashAlgorithmType.Sha512, bundle, policy, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (bundle.DsseEnvelope is null)
                {
                    logger.LogDebug("No DSSE envelope found in bundle for {Package}@{Version}", packageName, version);
                    return (new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.PayloadDecodeFailed }, null);
                }

                (success, result) = await verifier.TryVerifyAsync(
                    bundle.DsseEnvelope.Payload, bundle, policy, cancellationToken).ConfigureAwait(false);
            }

            if (!success)
            {
                logger.LogWarning(
                    "Sigstore verification failed for {Package}@{Version}: {FailureReason}",
                    packageName, version, result?.FailureReason);
                return (new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed }, null);
            }

            logger.LogDebug(
                "Sigstore verification passed for {Package}@{Version}. Signed by: {Signer}",
                packageName, version, result?.SignerIdentity?.SubjectAlternativeName);

            return (null, result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sigstore verification threw an exception for {Package}@{Version}", packageName, version);
            return (new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.AttestationParseFailed }, null);
        }
    }

    /// <summary>
    /// Extracts provenance data from a verified Sigstore result using the in-toto statement
    /// and Fulcio certificate extensions, avoiding manual JSON parsing of the DSSE payload.
    /// </summary>
    internal static NpmProvenanceData? ExtractProvenanceFromResult(VerificationResult result)
    {
        var extensions = result.SignerIdentity?.Extensions;
        var statement = result.Statement;

        // Extract SLSA-specific fields from the in-toto statement predicate.
        string? workflowPath = null;
        string? buildType = null;
        string? builderId = null;
        string? sourceRepository = null;
        string? workflowRef = null;

        if (statement?.PredicateType == SlsaProvenancePredicateType && statement.Predicate is { } predicate)
        {
            if (predicate.ValueKind == JsonValueKind.Object)
            {
                if (predicate.TryGetProperty("buildDefinition", out var buildDefinition) &&
                    buildDefinition.ValueKind == JsonValueKind.Object)
                {
                    if (buildDefinition.TryGetProperty("buildType", out var buildTypeElement) &&
                        buildTypeElement.ValueKind == JsonValueKind.String)
                    {
                        buildType = buildTypeElement.GetString();
                    }

                    if (buildDefinition.TryGetProperty("externalParameters", out var extParams) &&
                        extParams.ValueKind == JsonValueKind.Object &&
                        extParams.TryGetProperty("workflow", out var workflow) &&
                        workflow.ValueKind == JsonValueKind.Object)
                    {
                        if (workflow.TryGetProperty("repository", out var repoEl) &&
                            repoEl.ValueKind == JsonValueKind.String)
                        {
                            sourceRepository = repoEl.GetString();
                        }

                        if (workflow.TryGetProperty("path", out var pathEl) &&
                            pathEl.ValueKind == JsonValueKind.String)
                        {
                            workflowPath = pathEl.GetString();
                        }

                        if (workflow.TryGetProperty("ref", out var refEl) &&
                            refEl.ValueKind == JsonValueKind.String)
                        {
                            workflowRef = refEl.GetString();
                        }
                    }
                }

                if (predicate.TryGetProperty("runDetails", out var runDetails) &&
                    runDetails.ValueKind == JsonValueKind.Object &&
                    runDetails.TryGetProperty("builder", out var builder) &&
                    builder.ValueKind == JsonValueKind.Object)
                {
                    if (builder.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String)
                    {
                        builderId = idEl.GetString();
                    }
                }
            }
        }

        // Prefer certificate extensions for source repository and ref when available,
        // as they are cryptographically bound to the signing certificate.
        return new NpmProvenanceData
        {
            SourceRepository = extensions?.SourceRepositoryUri ?? sourceRepository,
            WorkflowPath = workflowPath,
            WorkflowRef = extensions?.SourceRepositoryRef ?? workflowRef,
            BuilderId = builderId,
            BuildType = buildType
        };
    }

    /// <summary>
    /// Verifies that the extracted provenance fields match the expected values.
    /// Source repository is already verified cryptographically via CertificateExtensionPolicy
    /// during Sigstore bundle verification, but is also checked here for defense-in-depth.
    /// </summary>
    internal static ProvenanceVerificationResult VerifyProvenanceFields(
        NpmProvenanceData provenance,
        string expectedSourceRepository,
        string expectedWorkflowPath,
        string expectedBuildType,
        Func<WorkflowRefInfo, bool>? validateWorkflowRef)
    {
        if (!string.Equals(provenance.SourceRepository, expectedSourceRepository, StringComparison.OrdinalIgnoreCase))
        {
            return new ProvenanceVerificationResult
            {
                Outcome = ProvenanceVerificationOutcome.SourceRepositoryMismatch,
                Provenance = provenance
            };
        }

        if (!string.Equals(provenance.WorkflowPath, expectedWorkflowPath, StringComparison.Ordinal))
        {
            return new ProvenanceVerificationResult
            {
                Outcome = ProvenanceVerificationOutcome.WorkflowMismatch,
                Provenance = provenance
            };
        }

        if (!string.Equals(provenance.BuildType, expectedBuildType, StringComparison.Ordinal))
        {
            return new ProvenanceVerificationResult
            {
                Outcome = ProvenanceVerificationOutcome.BuildTypeMismatch,
                Provenance = provenance
            };
        }

        if (validateWorkflowRef is not null)
        {
            if (!WorkflowRefInfo.TryParse(provenance.WorkflowRef, out var refInfo) || refInfo is null)
            {
                return new ProvenanceVerificationResult
                {
                    Outcome = ProvenanceVerificationOutcome.WorkflowRefMismatch,
                    Provenance = provenance
                };
            }

            if (!validateWorkflowRef(refInfo))
            {
                return new ProvenanceVerificationResult
                {
                    Outcome = ProvenanceVerificationOutcome.WorkflowRefMismatch,
                    Provenance = provenance
                };
            }
        }

        return new ProvenanceVerificationResult
        {
            Outcome = ProvenanceVerificationOutcome.Verified,
            Provenance = provenance
        };
    }

    /// <summary>
    /// Parses a GitHub repository URL into owner and repo components.
    /// </summary>
    internal static bool TryParseGitHubOwnerRepo(string repositoryUrl, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        owner = segments[0];
        repo = segments[1];
        return true;
    }
}
