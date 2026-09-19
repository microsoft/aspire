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
    /// Installation was skipped because a prerequisite (npm) is not available.
    /// </summary>
    Skipped,

    /// <summary>
    /// Installation failed.
    /// </summary>
    Failed
}

/// <summary>
/// Contains the generated skill files, or the reason acquisition or generation did not succeed.
/// </summary>
internal sealed record PlaywrightInstallResult(
    PlaywrightInstallStatus Status,
    IReadOnlyList<AgentSkillFile> Files,
    string? Message);

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
    /// The version range to resolve. Accepts any version from 0.1.3 onwards.
    /// </summary>
    internal const string VersionRange = ">=0.1.3";

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
    /// Installs the verified Playwright CLI and generates its skill in an isolated workspace.
    /// </summary>
    public async Task<PlaywrightInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await interactionService.ShowStatusAsync(
                AgentCommandStrings.PlaywrightCliInstaller_InstallingStatus,
                async () =>
                {
                    var (status, message) = await InstallCoreAsync(cancellationToken);
                    if (status is not PlaywrightInstallStatus.Installed)
                    {
                        return new PlaywrightInstallResult(status, [], message);
                    }

                    return await GenerateSkillFilesAsync(cancellationToken);
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to install Playwright CLI or generate its skill files.");
            return new PlaywrightInstallResult(
                PlaywrightInstallStatus.Failed,
                [],
                string.Format(CultureInfo.CurrentCulture, AgentSkillInstallerStrings.PlaywrightInstallationFailed, ex.Message));
        }
    }

    private async Task<(PlaywrightInstallStatus Status, string? Message)> InstallCoreAsync(CancellationToken cancellationToken)
    {
        // Early exit if npm is not available — playwright-cli requires npm.
        if (!npmRunner.IsAvailable)
        {
            logger.LogDebug("npm is not available on PATH, skipping Playwright CLI installation.");
            return (PlaywrightInstallStatus.Skipped, AgentSkillInstallerStrings.PlaywrightNpmRequired);
        }

        // Step 1: Resolve the target version from the public npm registry.
        var versionOverride = configuration[VersionOverrideKey];
        string effectiveRange;

        if (!string.IsNullOrEmpty(versionOverride))
        {
            // The override is forwarded directly to npm as an exact version specifier, so reject
            // anything that is not a strict SemVer 2.0 version (e.g. ranges like ">=1.0.0", npm
            // dist-tags like "latest", or arbitrary strings). This prevents a malformed config
            // value from being interpreted by npm in unexpected ways and gives the user a clear
            // error rather than a generic resolve failure.
            // See https://semver.org/spec/v2.0.0.html for the accepted shape.
            if (!SemVersion.TryParse(versionOverride, SemVersionStyles.Strict, out _))
            {
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_InvalidVersionOverride, VersionOverrideKey, versionOverride));
            }

            effectiveRange = versionOverride;
            logger.LogDebug("Using version override from '{ConfigKey}': {Version}", VersionOverrideKey, versionOverride);
        }
        else
        {
            effectiveRange = VersionRange;
        }

        logger.LogDebug("Resolving {Package}@{Range} from the public npm registry.", PackageName, effectiveRange);
        var packageInfo = await npmRunner.ResolvePackageAsync(PackageName, effectiveRange, cancellationToken);

        if (packageInfo is null)
        {
            return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToResolvePackage, NpmPackageInfo.FormatPackageSpecifier(PackageName, effectiveRange)));
        }

        logger.LogDebug("Resolved {PackageSpecifier}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));

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

                // The caller still generates the skill, even when the binary needs no update.
                return (PlaywrightInstallStatus.Installed, null);
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

        // Step 3: Download the tarball via npm pack.
        var tempDir = Directory.CreateTempSubdirectory("aspire-playwright-").FullName;

        try
        {
            logger.LogDebug("Downloading {PackageSpecifier} to {TempDir}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version), tempDir);
            var tarballPath = await npmRunner.PackAsync(PackageName, packageInfo.Version.ToString(), tempDir, cancellationToken);

            if (tarballPath is null)
            {
                logger.LogWarning("Failed to download {PackageSpecifier}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToDownload, NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version)));
            }

            if (!validationDisabled)
            {
                // Step 4: Verify provenance via Sigstore bundle verification and SLSA attestation checks.
                // The digest is computed from the downloaded archive so verification binds the
                // exact package bytes to the signed provenance statement.
                var tarballIntegrity = ComputeIntegrity(tarballPath);
                logger.LogDebug("Verifying provenance for {PackageSpecifier}.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
                var provenanceResult = await provenanceChecker.VerifyProvenanceAsync(
                    PackageName,
                    packageInfo.Version.ToString(),
                    ExpectedSourceRepository,
                    ExpectedWorkflowPath,
                    ExpectedBuildType,
                    refInfo => string.Equals(refInfo.Kind, "tags", StringComparison.Ordinal) &&
                               (string.Equals(refInfo.Name, $"{packageInfo.Version}", StringComparison.Ordinal) ||
                                string.Equals(refInfo.Name, $"v{packageInfo.Version}", StringComparison.Ordinal)),
                    tarballIntegrity,
                    cancellationToken);

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

            // Step 5: Install globally from the verified tarball.
            logger.LogDebug("Installing {PackageSpecifier} globally.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
            var installSuccess = await npmRunner.InstallGlobalAsync(tarballPath, cancellationToken);

            if (!installSuccess)
            {
                logger.LogWarning("Failed to install {PackageSpecifier} globally.", NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version));
                return (PlaywrightInstallStatus.Failed, string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToInstallGlobally, NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version)));
            }

            return (PlaywrightInstallStatus.Installed, null);
        }
        finally
        {
            CleanupTemporaryDirectory(tempDir);
        }
    }

    private async Task<PlaywrightInstallResult> GenerateSkillFilesAsync(CancellationToken cancellationToken)
    {
        // playwright-cli always writes .claude/skills, regardless of the selected clients.
        // Generate once outside the user's workspace and home; only the captured payload is
        // distributed. Cleanup is consequently limited to a directory this invocation owns.
        var workspace = Directory.CreateTempSubdirectory("aspire-playwright-skills-");
        try
        {
            logger.LogDebug("Generating Playwright CLI skill files in {Workspace}.", workspace.FullName);
            if (!await playwrightCliRunner.InstallSkillsAsync(workspace.FullName, cancellationToken))
            {
                return new PlaywrightInstallResult(
                    PlaywrightInstallStatus.Failed, [], AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var skillDirectory = new DirectoryInfo(Path.Combine(workspace.FullName, s_primarySkillBaseDirectory, PlaywrightCliSkillName));
            if (!File.Exists(Path.Combine(skillDirectory.FullName, "SKILL.md")))
            {
                return new PlaywrightInstallResult(
                    PlaywrightInstallStatus.Failed, [], AgentSkillInstallerStrings.PlaywrightMissingSkill);
            }

            // Do not follow generated links outside the owned workspace or omit linked
            // references and then claim that the complete skill was captured.
            for (var directory = skillDirectory; directory.FullName != workspace.FullName; directory = directory.Parent!)
            {
                RejectSymbolicLink(directory);
            }

            List<AgentSkillFile> files = [];
            Stack<DirectoryInfo> pending = new();
            pending.Push(skillDirectory);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectSymbolicLink(entry);

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else
                    {
                        files.Add(new AgentSkillFile(
                            Path.GetRelativePath(skillDirectory.FullName, entry.FullName),
                            await File.ReadAllBytesAsync(entry.FullName, cancellationToken)));
                    }
                }
            }

            if (!files.Any(static file => file.RelativePath == "SKILL.md" && file.Content.Length > 0))
            {
                return new PlaywrightInstallResult(
                    PlaywrightInstallStatus.Failed, [], AgentSkillInstallerStrings.PlaywrightMissingSkill);
            }

            return new PlaywrightInstallResult(
                PlaywrightInstallStatus.Installed,
                files.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray(),
                null);
        }
        finally
        {
            CleanupTemporaryDirectory(workspace.FullName);
        }
    }

    private static void RejectSymbolicLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(string.Format(
                CultureInfo.CurrentCulture, AgentSkillInstallerStrings.PlaywrightLinkedSkillEntry, entry.FullName));
        }
    }

    private void CleanupTemporaryDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to clean up temporary directory: {TempDir}", directory);
        }
    }

    /// <summary>
    /// Computes the SHA-512 SRI integrity value for a file.
    /// </summary>
    internal static string ComputeIntegrity(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return $"sha512-{Convert.ToBase64String(SHA512.HashData(stream))}";
    }
}
