// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Agents.Playwright;

/// <summary>
/// Orchestrates secure installation of the Playwright CLI with supply chain verification.
/// </summary>
internal sealed class PlaywrightCliInstaller(
    INpmRunner npmRunner,
    INpmProvenanceChecker provenanceChecker,
    IPlaywrightCliRunner playwrightCliRunner,
    IInteractionService interactionService,
    IConfiguration configuration,
    ILogger<PlaywrightCliInstaller> logger)
{
    /// <summary>
    /// The npm package name for the Playwright CLI.
    /// </summary>
    internal const string PackageName = "@playwright/cli";

    /// <summary>
    /// The version range to resolve. Accepts any version from 0.1.1 onwards.
    /// </summary>
    internal const string VersionRange = ">=0.1.1";

    /// <summary>
    /// The expected source repository for provenance verification.
    /// </summary>
    internal const string ExpectedSourceRepository = "https://github.com/microsoft/playwright-cli";

    /// <summary>
    /// The expected workflow file path in the source repository.
    /// </summary>
    internal const string ExpectedWorkflowPath = ".github/workflows/publish.yml";

    /// <summary>
    /// The expected SLSA build type, which identifies GitHub Actions as the CI system
    /// and implicitly confirms the OIDC token issuer is <c>https://token.actions.githubusercontent.com</c>.
    /// </summary>
    internal const string ExpectedBuildType = "https://slsa-framework.github.io/github-actions-buildtypes/workflow/v1";

    /// <summary>
    /// The name of the playwright-cli skill directory.
    /// </summary>
    internal const string PlaywrightCliSkillName = "playwright-cli";

    /// <summary>
    /// The primary skill base directory where playwright-cli installs skills.
    /// This must match the directory that the playwright-cli binary actually writes to.
    /// See: https://github.com/microsoft/playwright-cli/issues/294
    /// </summary>
    internal static readonly string s_primarySkillBaseDirectory = Path.Combine(".claude", "skills");

    /// <summary>
    /// Configuration key that disables package validation when set to "true".
    /// This is a break-glass mechanism for debugging npm service issues and must never be the default.
    /// </summary>
    internal const string DisablePackageValidationKey = "disablePlaywrightCliPackageValidation";

    /// <summary>
    /// Configuration key that overrides the version to install. When set, the specified
    /// exact version is used instead of resolving the latest from the version range.
    /// </summary>
    internal const string VersionOverrideKey = "playwrightCliVersion";

    /// <summary>
    /// Installs the Playwright CLI with supply chain verification and generates skill files.
    /// </summary>
    /// <param name="context">The agent environment scan context containing detected skill directories.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if installation succeeded or was skipped (already up-to-date), false on failure.</returns>
    public async Task<bool> InstallAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        return await interactionService.ShowStatusAsync(
            "Installing Playwright CLI...",
            () => InstallCoreAsync(context, cancellationToken));
    }

    private async Task<bool> InstallCoreAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        // Step 1: Resolve the target version and integrity hash from the npm registry.
        var versionOverride = configuration[VersionOverrideKey];
        var effectiveRange = !string.IsNullOrEmpty(versionOverride) ? versionOverride : VersionRange;

        if (!string.IsNullOrEmpty(versionOverride))
        {
            logger.LogDebug("Using version override from '{ConfigKey}': {Version}", VersionOverrideKey, versionOverride);
        }

        logger.LogDebug("Resolving {Package}@{Range} from npm registry", PackageName, effectiveRange);
        var packageInfo = await npmRunner.ResolvePackageAsync(PackageName, effectiveRange, cancellationToken);

        if (packageInfo is null)
        {
            logger.LogWarning("Failed to resolve {Package}@{Range} from npm registry. Is npm installed?", PackageName, VersionRange);
            return false;
        }

        logger.LogDebug("Resolved {Package}@{Version} with integrity {Integrity}", PackageName, packageInfo.Version, packageInfo.Integrity);

        // Step 2: Check if a suitable version is already installed.
        var installedVersion = await playwrightCliRunner.GetVersionAsync(cancellationToken);
        if (installedVersion is not null)
        {
            var comparison = SemVersion.ComparePrecedence(installedVersion, packageInfo.Version);
            if (comparison >= 0)
            {
                logger.LogDebug(
                    "playwright-cli {InstalledVersion} is already installed (target: {TargetVersion}), skipping installation",
                    installedVersion,
                    packageInfo.Version);

                // Still install skills in case they're missing.
                var skillsInstalled = await playwrightCliRunner.InstallSkillsAsync(context.RepositoryRoot.FullName, cancellationToken);
                if (skillsInstalled)
                {
                    MirrorSkillFiles(context);
                }
                return skillsInstalled;
            }

            logger.LogDebug(
                "Upgrading playwright-cli from {InstalledVersion} to {TargetVersion}",
                installedVersion,
                packageInfo.Version);
        }

        // Check break-glass configuration to bypass package validation.
        var validationDisabled = string.Equals(configuration[DisablePackageValidationKey], "true", StringComparison.OrdinalIgnoreCase);
        if (validationDisabled)
        {
            logger.LogWarning(
                "Package validation is disabled via '{ConfigKey}'. " +
                "Sigstore attestation, provenance, and integrity checks will be skipped. " +
                "This should only be used for debugging npm service issues.",
                DisablePackageValidationKey);
        }

        if (!validationDisabled)
        {
            // Step 3: Verify provenance via Sigstore bundle verification and SLSA attestation checks.
            // This cryptographically verifies the Sigstore bundle (Fulcio CA, Rekor tlog, OIDC identity)
            // and then checks the provenance fields (source repo, workflow, build type, ref).
            logger.LogDebug("Verifying provenance for {Package}@{Version}", PackageName, packageInfo.Version);
            var provenanceResult = await provenanceChecker.VerifyProvenanceAsync(
                PackageName,
                packageInfo.Version.ToString(),
                ExpectedSourceRepository,
                ExpectedWorkflowPath,
                ExpectedBuildType,
                refInfo => string.Equals(refInfo.Kind, "tags", StringComparison.Ordinal) &&
                           string.Equals(refInfo.Name, $"v{packageInfo.Version}", StringComparison.Ordinal),
                cancellationToken,
                sriIntegrity: packageInfo.Integrity);

            if (!provenanceResult.IsVerified)
            {
                logger.LogWarning(
                    "Provenance verification failed for {Package}@{Version}: {Outcome}. Expected source repository: {ExpectedRepo}",
                    PackageName,
                    packageInfo.Version,
                    provenanceResult.Outcome,
                    ExpectedSourceRepository);
                return false;
            }

            logger.LogDebug(
                "Provenance verification passed for {Package}@{Version} (source: {SourceRepo})",
                PackageName,
                packageInfo.Version,
                provenanceResult.Provenance?.SourceRepository);
        }

        // Step 4: Download the tarball via npm pack.
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            logger.LogDebug("Downloading {Package}@{Version} to {TempDir}", PackageName, packageInfo.Version, tempDir);
            var tarballPath = await npmRunner.PackAsync(PackageName, packageInfo.Version.ToString(), tempDir, cancellationToken);

            if (tarballPath is null)
            {
                logger.LogWarning("Failed to download {Package}@{Version}", PackageName, packageInfo.Version);
                return false;
            }

            // Step 5: Verify the downloaded tarball's SHA-512 hash matches the SRI integrity value.
            if (!validationDisabled && !VerifyIntegrity(tarballPath, packageInfo.Integrity))
            {
                logger.LogWarning(
                    "Integrity verification failed for {Package}@{Version}. The downloaded package may have been tampered with.",
                    PackageName,
                    packageInfo.Version);
                return false;
            }

            if (!validationDisabled)
            {
                logger.LogDebug("Integrity verification passed for {TarballPath}", tarballPath);
            }

            // Step 6: Install globally from the verified tarball.
            logger.LogDebug("Installing {Package}@{Version} globally", PackageName, packageInfo.Version);
            var installSuccess = await npmRunner.InstallGlobalAsync(tarballPath, cancellationToken);

            if (!installSuccess)
            {
                logger.LogWarning("Failed to install {Package}@{Version} globally", PackageName, packageInfo.Version);
                return false;
            }

            // Step 7: Generate skill files.
            logger.LogDebug("Generating Playwright CLI skill files");
            var skillsResult = await playwrightCliRunner.InstallSkillsAsync(context.RepositoryRoot.FullName, cancellationToken);
            if (skillsResult)
            {
                MirrorSkillFiles(context);
            }
            return skillsResult;
        }
        finally
        {
            // Clean up temporary directory.
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Failed to clean up temporary directory: {TempDir}", tempDir);
            }
        }
    }

    /// <summary>
    /// Mirrors the playwright-cli skill directory from the primary location to all other
    /// detected agent environment skill directories so that every configured environment
    /// has an identical copy of the skill files.
    /// </summary>
    private void MirrorSkillFiles(AgentEnvironmentScanContext context)
    {
        var repoRoot = context.RepositoryRoot.FullName;
        var primarySkillDir = Path.Combine(repoRoot, s_primarySkillBaseDirectory, PlaywrightCliSkillName);

        if (!Directory.Exists(primarySkillDir))
        {
            logger.LogDebug("Primary skill directory does not exist: {PrimarySkillDir}", primarySkillDir);
            return;
        }

        foreach (var skillBaseDir in context.SkillBaseDirectories)
        {
            // Skip the primary directory — it's the source
            if (string.Equals(skillBaseDir, s_primarySkillBaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetSkillDir = Path.Combine(repoRoot, skillBaseDir, PlaywrightCliSkillName);

            try
            {
                SyncDirectory(primarySkillDir, targetSkillDir);
                logger.LogDebug("Mirrored playwright-cli skills to {TargetDir}", targetSkillDir);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to mirror playwright-cli skills to {TargetDir}", targetSkillDir);
            }
        }
    }

    /// <summary>
    /// Synchronizes the contents of the source directory to the target directory,
    /// creating, updating, and removing files so the target matches the source exactly.
    /// </summary>
    internal static void SyncDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Copy all files from source to target
        foreach (var sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            var targetFile = Path.Combine(targetDir, relativePath);

            var targetFileDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetFileDir))
            {
                Directory.CreateDirectory(targetFileDir);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }

        // Remove files in target that don't exist in source
        if (Directory.Exists(targetDir))
        {
            foreach (var targetFile in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(targetDir, targetFile);
                var sourceFile = Path.Combine(sourceDir, relativePath);

                if (!File.Exists(sourceFile))
                {
                    File.Delete(targetFile);
                }
            }

            // Remove empty directories in target
            foreach (var dir in Directory.GetDirectories(targetDir, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length))
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                }
            }
        }
    }

    /// <summary>
    /// Verifies that the SHA-512 hash of the file matches the SRI integrity string.
    /// </summary>
    internal static bool VerifyIntegrity(string filePath, string sriIntegrity)
    {
        // SRI format: "sha512-<base64hash>"
        if (!sriIntegrity.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedHash = sriIntegrity["sha512-".Length..];

        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA512.HashData(stream);
        var actualHash = Convert.ToBase64String(hashBytes);

        return string.Equals(expectedHash, actualHash, StringComparison.Ordinal);
    }
}
