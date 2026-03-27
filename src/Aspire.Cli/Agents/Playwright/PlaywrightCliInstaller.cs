// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Agents.Playwright;

/// <summary>
/// Describes the outcome of a Playwright CLI installation attempt.
/// </summary>
internal enum PlaywrightInstallStatus
{
    /// <summary>
    /// Installation completed successfully.
    /// </summary>
    Installed,

    /// <summary>
    /// Installation completed but some post-install steps (e.g., mirroring) had warnings.
    /// </summary>
    InstalledWithWarnings,

    /// <summary>
    /// Installation was skipped because a prerequisite (npm) is not available.
    /// </summary>
    Skipped,

    /// <summary>
    /// Installation failed.
    /// </summary>
    Failed
}

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
    /// <param name="repoRoot">The workspace/repository root directory.</param>
    /// <param name="selectedSkillDirectories">The skill directories the user explicitly selected.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple where <c>Status</c> indicates the outcome (installed, skipped, or failed) and <c>Message</c> contains additional details when applicable.</returns>
    public async Task<(PlaywrightInstallStatus Status, string? Message)> InstallAsync(string repoRoot, IReadOnlySet<string> selectedSkillDirectories, CancellationToken cancellationToken)
    {
        return await interactionService.ShowStatusAsync(
            AgentCommandStrings.PlaywrightCliInstaller_InstallingStatus,
            () => InstallCoreAsync(repoRoot, selectedSkillDirectories, cancellationToken));
    }

    private async Task<(PlaywrightInstallStatus Status, string? Message)> InstallCoreAsync(string repoRoot, IReadOnlySet<string> selectedSkillDirectories, CancellationToken cancellationToken)
    {
        // Early exit if npm is not available — playwright-cli requires npm.
        if (!npmRunner.IsAvailable)
        {
            logger.LogDebug("npm is not available on PATH, skipping Playwright CLI installation.");
            return (PlaywrightInstallStatus.Skipped, null);
        }

        // Step 1: Resolve the target version and integrity hash from the npm registry.
        var versionOverride = configuration[VersionOverrideKey];
        var effectiveRange = !string.IsNullOrEmpty(versionOverride) ? versionOverride : VersionRange;

        if (!string.IsNullOrEmpty(versionOverride))
        {
            logger.LogDebug("Using version override from '{ConfigKey}': {Version}", VersionOverrideKey, versionOverride);
        }

        logger.LogDebug("Resolving {Package}@{Range} from npm registry.", PackageName, effectiveRange);
        var packageInfo = await npmRunner.ResolvePackageAsync(PackageName, effectiveRange, cancellationToken);

        if (packageInfo is null)
        {
            return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToResolvePackage, NpmPackageInfo.FormatPackageSpecifier(PackageName, effectiveRange)));
        }

        logger.LogDebug("Resolved {PackageSpecifier} with integrity {Integrity}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version), packageInfo.Integrity);

        // Step 2: Check if a suitable version is already installed.
        var installedVersion = await playwrightCliRunner.GetVersionAsync(cancellationToken);
        if (installedVersion is not null)
        {
            var comparison = SemVersion.ComparePrecedence(installedVersion, packageInfo.Version);
            if (comparison >= 0)
            {
                logger.LogDebug(
                    "playwright-cli {InstalledVersion} is already installed (target: {TargetVersion}), skipping installation.",
                    installedVersion,
                    packageInfo.Version);

                // Still install skills in case they're missing.
                return await InstallAndMirrorSkillsAsync(repoRoot, selectedSkillDirectories, cancellationToken);
            }

            logger.LogDebug(
                "Upgrading playwright-cli from {InstalledVersion} to {TargetVersion}.",
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
            logger.LogDebug("Verifying provenance for {PackageSpecifier}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
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
                    "Provenance verification failed for {PackageSpecifier}: {Outcome}. Expected source repository: {ExpectedRepo}",
                    NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version),
                    provenanceResult.Outcome,
                    ExpectedSourceRepository);
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_ProvenanceVerificationFailed, NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version), provenanceResult.Outcome));
            }

            logger.LogDebug(
                "Provenance verification passed for {PackageSpecifier} (source: {SourceRepo})",
                NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version),
                provenanceResult.Provenance?.SourceRepository);
        }

        // Step 4: Download the tarball via npm pack.
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            logger.LogDebug("Downloading {PackageSpecifier} to {TempDir}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version), tempDir);
            var tarballPath = await npmRunner.PackAsync(PackageName, packageInfo.Version.ToString(), tempDir, cancellationToken);

            if (tarballPath is null)
            {
                logger.LogWarning("Failed to download {PackageSpecifier}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToDownload, NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version)));
            }

            // Step 5: Verify the downloaded tarball's SHA-512 hash matches the SRI integrity value.
            if (!validationDisabled && !VerifyIntegrity(tarballPath, packageInfo.Integrity))
            {
                logger.LogWarning(
                    "Integrity verification failed for {PackageSpecifier}. The downloaded package may have been tampered with.",
                    NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_IntegrityVerificationFailed, NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version)));
            }

            if (!validationDisabled)
            {
                logger.LogDebug("Integrity verification passed for {TarballPath}.", tarballPath);
            }

            // Step 6: Install globally from the verified tarball.
            logger.LogDebug("Installing {PackageSpecifier} globally.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
            var installSuccess = await npmRunner.InstallGlobalAsync(tarballPath, cancellationToken);

            if (!installSuccess)
            {
                logger.LogWarning("Failed to install {PackageSpecifier} globally.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToInstallGlobally, NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version)));
            }

            // Step 7: Generate skill files and mirror to selected locations.
            return await InstallAndMirrorSkillsAsync(repoRoot, selectedSkillDirectories, cancellationToken);
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
    /// Runs <c>playwright-cli install --skills</c>, then mirrors the generated files
    /// to the user-selected locations and cleans up any unselected locations that
    /// playwright-cli created during this run.
    /// </summary>
    private async Task<(PlaywrightInstallStatus Status, string? Message)> InstallAndMirrorSkillsAsync(
        string repoRoot,
        IReadOnlySet<string> selectedSkillDirectories,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Generating Playwright CLI skill files.");
        var preExisting = SnapshotPlaywrightSkillDirs(repoRoot);
        var skillsInstalled = await playwrightCliRunner.InstallSkillsAsync(repoRoot, cancellationToken);
        if (!skillsInstalled)
        {
            return (PlaywrightInstallStatus.Failed, AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles);
        }

        try
        {
            MirrorSkillFiles(repoRoot, selectedSkillDirectories, preExisting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to mirror Playwright CLI skill files to some locations.");
            return (PlaywrightInstallStatus.InstalledWithWarnings, AgentCommandStrings.PlaywrightCliInstaller_InstalledWithMirrorWarnings);
        }

        return (PlaywrightInstallStatus.Installed, null);
    }

    /// <summary>
    /// Snapshots which playwright-cli skill directories already exist across all
    /// known skill locations so we can tell what was created during this run.
    /// </summary>
    private static HashSet<string> SnapshotPlaywrightSkillDirs(string repoRoot)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in SkillLocation.All)
        {
            var dir = Path.Combine(repoRoot, location.RelativeSkillDirectory, PlaywrightCliSkillName);
            if (Directory.Exists(dir))
            {
                existing.Add(location.RelativeSkillDirectory);
            }
        }
        return existing;
    }

    /// <summary>
    /// Mirrors the playwright-cli skill directory from the primary location to all
    /// user-selected skill directories, then cleans up any directories that
    /// playwright-cli created in unselected locations during this run.
    /// </summary>
    private void MirrorSkillFiles(string repoRoot, IReadOnlySet<string> selectedSkillDirectories, HashSet<string> preExistingLocations)
    {
        var primarySkillDir = Path.Combine(repoRoot, s_primarySkillBaseDirectory, PlaywrightCliSkillName);

        if (!Directory.Exists(primarySkillDir))
        {
            logger.LogDebug("Primary skill directory does not exist: {PrimarySkillDir}", primarySkillDir);
            return;
        }

        // Mirror to each user-selected location (skip the primary — it's the source).
        foreach (var skillBaseDir in selectedSkillDirectories)
        {
            if (string.Equals(skillBaseDir, s_primarySkillBaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetSkillDir = Path.Combine(repoRoot, skillBaseDir, PlaywrightCliSkillName);

            try
            {
                SyncDirectory(primarySkillDir, targetSkillDir);
                logger.LogDebug("Mirrored playwright-cli skills to {TargetDir}.", targetSkillDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Failed to mirror playwright-cli skills to {TargetDir}.", targetSkillDir);
            }
        }

        // Clean up playwright-cli directories that were created during this run
        // in locations the user didn't select. We only remove directories that
        // didn't exist before install — pre-existing content is never touched.
        foreach (var location in SkillLocation.All)
        {
            if (selectedSkillDirectories.Contains(location.RelativeSkillDirectory))
            {
                continue; // User selected this location — keep it
            }

            if (preExistingLocations.Contains(location.RelativeSkillDirectory))
            {
                continue; // Was already there before this run — leave it alone
            }

            var skillDir = Path.Combine(repoRoot, location.RelativeSkillDirectory, PlaywrightCliSkillName);
            if (!Directory.Exists(skillDir))
            {
                continue;
            }

            try
            {
                Directory.Delete(skillDir, recursive: true);
                logger.LogDebug("Removed playwright-cli skills from unselected location: {SkillDir}", skillDir);

                RemoveEmptyParentDirectories(skillDir, repoRoot, location.RelativeSkillDirectory, logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed to remove playwright-cli skills from {SkillDir}.", skillDir);
            }
        }
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> and removes empty parent directories,
    /// stopping at <paramref name="stopDir"/> (never deleted). The number of levels walked
    /// is bounded by the segment count in <paramref name="relativeSkillDirectory"/> + 1
    /// as an additional safeguard against unintended recursion.
    /// </summary>
    internal static void RemoveEmptyParentDirectories(string startDir, string stopDir, string relativeSkillDirectory, ILogger? logger = null)
    {
        var maxDepth = relativeSkillDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
        var depth = 0;
        var parent = Path.GetDirectoryName(startDir);
        while (parent is not null
            && ++depth <= maxDepth
            && !string.Equals(parent, stopDir, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(parent)
            && Directory.GetFileSystemEntries(parent).Length == 0)
        {
            Directory.Delete(parent);
            logger?.LogDebug("Removed empty directory: {Dir}", parent);
            parent = Path.GetDirectoryName(parent);
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
