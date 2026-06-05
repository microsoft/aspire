// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Npm;

/// <summary>
/// Represents the outcome of a provenance verification check.
/// Each value corresponds to a specific gate in the verification process.
/// </summary>
internal enum ProvenanceVerificationOutcome
{
    /// <summary>
    /// All checks passed and the source repository matches the expected value.
    /// </summary>
    Verified,

    /// <summary>
    /// Failed to fetch attestation data from the npm registry (network error or non-success HTTP status).
    /// </summary>
    AttestationFetchFailed,

    /// <summary>
    /// The attestation response could not be parsed as valid JSON.
    /// </summary>
    AttestationParseFailed,

    /// <summary>
    /// No SLSA provenance attestation was found in the registry response.
    /// </summary>
    SlsaProvenanceNotFound,

    /// <summary>
    /// The DSSE envelope payload could not be decoded from the attestation bundle.
    /// </summary>
    PayloadDecodeFailed,

    /// <summary>
    /// The source repository could not be extracted from the provenance statement.
    /// </summary>
    SourceRepositoryNotFound,

    /// <summary>
    /// The attested source repository does not match the expected value.
    /// </summary>
    SourceRepositoryMismatch,

    /// <summary>
    /// The attested workflow path does not match the expected value.
    /// </summary>
    WorkflowMismatch,

    /// <summary>
    /// The SLSA build type does not match the expected GitHub Actions build type,
    /// indicating the package was not built by the expected CI system.
    /// </summary>
    BuildTypeMismatch,

    /// <summary>
    /// The workflow ref did not pass the caller-provided validation callback,
    /// indicating the build was not triggered from the expected release tag.
    /// </summary>
    WorkflowRefMismatch
}

/// <summary>
/// Represents the deserialized provenance data extracted from an SLSA attestation.
/// </summary>
internal sealed class NpmProvenanceData
{
    /// <summary>
    /// Gets the source repository URL from the attestation (e.g., "https://github.com/microsoft/playwright-cli").
    /// </summary>
    public string? SourceRepository { get; init; }

    /// <summary>
    /// Gets the workflow file path from the attestation (e.g., ".github/workflows/publish.yml").
    /// </summary>
    public string? WorkflowPath { get; init; }

    /// <summary>
    /// Gets the builder ID URI from the attestation (e.g., "https://github.com/actions/runner/github-hosted").
    /// </summary>
    public string? BuilderId { get; init; }

    /// <summary>
    /// Gets the workflow reference (e.g., "refs/tags/v0.1.1").
    /// </summary>
    public string? WorkflowRef { get; init; }

    /// <summary>
    /// Gets the SLSA build type URI which identifies the CI system used to build the package
    /// (e.g., "https://slsa-framework.github.io/github-actions-buildtypes/workflow/v1" for GitHub Actions).
    /// This implicitly confirms the OIDC token issuer (e.g., <c>https://token.actions.githubusercontent.com</c>).
    /// </summary>
    public string? BuildType { get; init; }
}

/// <summary>
/// Represents the result of a provenance verification check.
/// </summary>
internal sealed class ProvenanceVerificationResult
{
    /// <summary>
    /// Gets the outcome of the verification, indicating which gate passed or failed.
    /// </summary>
    public required ProvenanceVerificationOutcome Outcome { get; init; }

    /// <summary>
    /// Gets the deserialized provenance data, if available. May be partially populated
    /// depending on how far verification progressed before failure.
    /// </summary>
    public NpmProvenanceData? Provenance { get; init; }

    /// <summary>
    /// Gets a value indicating whether the verification succeeded.
    /// </summary>
    public bool IsVerified => Outcome is ProvenanceVerificationOutcome.Verified;
}

/// <summary>
/// Represents a parsed workflow ref from an SLSA provenance attestation.
/// A workflow ref like <c>refs/tags/v0.1.1</c> is decomposed into its kind (e.g., "tags")
/// and name (e.g., "v0.1.1") to enable structured validation by callers.
/// </summary>
/// <param name="Raw">The original unmodified ref string (e.g., <c>refs/tags/v0.1.1</c>).</param>
/// <param name="Kind">The ref kind (e.g., "tags", "heads"). Extracted from the second segment of the ref path.</param>
/// <param name="Name">The ref name after the kind prefix (e.g., "v0.1.1", "main").</param>
internal sealed record WorkflowRefInfo(string Raw, string Kind, string Name)
{
    /// <summary>
    /// Attempts to parse a git ref string into its structured components.
    /// Expected format: <c>refs/{kind}/{name}</c> (e.g., <c>refs/tags/v0.1.1</c>).
    /// </summary>
    /// <param name="refString">The raw ref string to parse.</param>
    /// <param name="refInfo">The parsed <see cref="WorkflowRefInfo"/> if successful.</param>
    /// <returns><c>true</c> if the ref was successfully parsed; <c>false</c> otherwise.</returns>
    public static bool TryParse(string? refString, out WorkflowRefInfo? refInfo)
    {
        refInfo = null;

        if (string.IsNullOrEmpty(refString))
        {
            return false;
        }

        // Expected format: refs/{kind}/{name...}
        // The name can contain slashes (e.g., refs/tags/@scope/pkg@1.0.0)
        if (!refString.StartsWith("refs/", StringComparison.Ordinal))
        {
            return false;
        }

        var afterRefs = refString["refs/".Length..];
        var slashIndex = afterRefs.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == afterRefs.Length - 1)
        {
            return false;
        }

        var kind = afterRefs[..slashIndex];
        var name = afterRefs[(slashIndex + 1)..];
        refInfo = new WorkflowRefInfo(refString, kind, name);
        return true;
    }
}

/// <summary>
/// Verifies npm package provenance by checking SLSA attestations from the npm registry.
/// </summary>
internal interface INpmProvenanceChecker
{
    /// <summary>
    /// Verifies that the SLSA provenance attestation for a package was built from the expected source repository,
    /// using the expected workflow, and with the expected build system.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@playwright/cli").</param>
    /// <param name="version">The exact version to verify.</param>
    /// <param name="expectedSourceRepository">The expected source repository URL (e.g., "https://github.com/microsoft/playwright-cli").</param>
    /// <param name="expectedWorkflowPath">The expected workflow file path (e.g., ".github/workflows/publish.yml").</param>
    /// <param name="expectedBuildType">The expected SLSA build type URI identifying the CI system.</param>
    /// <param name="validateWorkflowRef">
    /// An optional callback that validates the parsed workflow ref. The callback receives a <see cref="WorkflowRefInfo"/>
    /// with the ref decomposed into its kind and name. If <c>null</c>, the workflow ref gate is skipped.
    /// If the callback returns <c>false</c>, verification fails with <see cref="ProvenanceVerificationOutcome.WorkflowRefMismatch"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="sriIntegrity">
    /// An optional SRI integrity string (e.g., "sha512-...") for the package tarball.
    /// When provided, implementations that perform cryptographic verification can verify
    /// that the attestation covers this specific artifact digest.
    /// </param>
    /// <returns>A <see cref="ProvenanceVerificationResult"/> indicating the outcome and any extracted provenance data.</returns>
    Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, CancellationToken cancellationToken, string? sriIntegrity = null);
}
