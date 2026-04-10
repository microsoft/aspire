// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A fake implementation of <see cref="INpmRunner"/> for testing.
/// </summary>
internal sealed class FakeNpmRunner : INpmRunner
{
    public bool IsAvailable => true;

    public Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
        => Task.FromResult<NpmPackageInfo?>(null);

    public Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
        => Task.FromResult(true);
}

/// <summary>
/// A fake implementation of <see cref="INpmProvenanceChecker"/> for testing.
/// </summary>
internal sealed class FakeNpmProvenanceChecker : INpmProvenanceChecker
{
    public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, CancellationToken cancellationToken, string? sriIntegrity = null)
        => Task.FromResult(new ProvenanceVerificationResult
        {
            Outcome = ProvenanceVerificationOutcome.Verified,
            Provenance = new NpmProvenanceData { SourceRepository = expectedSourceRepository }
        });
}

/// <summary>
/// A fake implementation of <see cref="IPlaywrightCliRunner"/> for testing.
/// </summary>
internal sealed class FakePlaywrightCliRunner : IPlaywrightCliRunner
{
    public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        => Task.FromResult<SemVersion?>(null);

    public Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
