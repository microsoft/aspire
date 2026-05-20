// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Resolves, verifies, and caches Aspire workflow skills from the external Aspire skills package.
/// </summary>
internal sealed class AspireSkillsInstaller(
    INpmRunner npmRunner,
    INpmProvenanceChecker provenanceChecker,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IConfiguration configuration,
    AspireCliTelemetry telemetry,
    ILogger<AspireSkillsInstaller> logger) : IAspireSkillsInstaller
{
    internal const string PackageName = "@microsoft/aspire-skills";
    internal const string Version = "0.0.1";
    internal const string ExpectedSourceRepository = "https://github.com/microsoft/aspire-skills";
    internal const string ExpectedWorkflowPath = ".github/workflows/publish.yml";
    internal const string ExpectedBuildType = "https://slsa-framework.github.io/github-actions-buildtypes/workflow/v1";
    internal const string DisablePackageValidationKey = "disableAspireSkillsPackageValidation";
    internal const string VersionOverrideKey = "aspireSkillsVersion";
    internal const string MaxCacheAgeKey = "AspireSkillsMaxCacheAgeSeconds";

    private static readonly TimeSpan s_defaultMaxCacheAge = TimeSpan.FromDays(7);

    public Task<AspireSkillsInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        return interactionService.ShowStatusAsync(
            AgentCommandStrings.AspireSkillsInstaller_InstallingStatus,
            () => InstallCoreAsync(cancellationToken));
    }

    private async Task<AspireSkillsInstallResult> InstallCoreAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartReportedActivity("AspireSkillsInstaller.Install");

        var effectiveVersion = configuration[VersionOverrideKey];
        if (string.IsNullOrWhiteSpace(effectiveVersion))
        {
            effectiveVersion = Version;
        }

        activity?.SetTag("aspire.skills.package", PackageName);
        activity?.SetTag("aspire.skills.version", effectiveVersion);

        var cacheRoot = GetCacheRoot();
        Directory.CreateDirectory(cacheRoot);

        var cachedBundle = await TryLoadCachedBundleAsync(cacheRoot, effectiveVersion, activity, cancellationToken).ConfigureAwait(false);
        if (cachedBundle is not null)
        {
            CleanupStaleCacheEntries(cacheRoot, effectiveVersion);
            return AspireSkillsInstallResult.Installed(cachedBundle);
        }

        if (!npmRunner.IsAvailable)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "npm is unavailable.");
            return AspireSkillsInstallResult.Failed(AgentCommandStrings.AspireSkillsInstaller_NpmUnavailable);
        }

        var packageInfo = await npmRunner.ResolvePackageAsync(PackageName, effectiveVersion, cancellationToken).ConfigureAwait(false);
        if (packageInfo is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Package resolution failed.");
            return AspireSkillsInstallResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToResolvePackage, NpmPackageInfo.FormatPackageSpecifier(PackageName, effectiveVersion)));
        }

        var packageSpecifier = NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version);
        var validationDisabled = string.Equals(configuration[DisablePackageValidationKey], "true", StringComparison.OrdinalIgnoreCase);
        if (!validationDisabled)
        {
            var provenanceResult = await provenanceChecker.VerifyProvenanceAsync(
                PackageName,
                packageInfo.Version.ToString(),
                ExpectedSourceRepository,
                ExpectedWorkflowPath,
                ExpectedBuildType,
                refInfo => string.Equals(refInfo.Kind, "tags", StringComparison.Ordinal) &&
                           (string.Equals(refInfo.Name, $"{packageInfo.Version}", StringComparison.Ordinal) ||
                            string.Equals(refInfo.Name, $"v{packageInfo.Version}", StringComparison.Ordinal)),
                cancellationToken,
                sriIntegrity: packageInfo.Integrity).ConfigureAwait(false);

            if (!provenanceResult.IsVerified)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Provenance verification failed.");
                return AspireSkillsInstallResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_ProvenanceVerificationFailed, packageSpecifier, provenanceResult.Outcome));
            }
        }

        var tempDir = Path.Combine(cacheRoot, $".tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var stageDir = Path.Combine(cacheRoot, $".stage-{Guid.NewGuid():N}");

        try
        {
            var tarballPath = await npmRunner.PackAsync(PackageName, packageInfo.Version.ToString(), tempDir, cancellationToken).ConfigureAwait(false);
            if (tarballPath is null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Package download failed.");
                return AspireSkillsInstallResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToDownload, packageSpecifier));
            }

            if (!validationDisabled && !VerifyIntegrity(tarballPath, packageInfo.Integrity))
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Package integrity verification failed.");
                return AspireSkillsInstallResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_IntegrityVerificationFailed, packageSpecifier));
            }

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            ExtractTarball(tarballPath, extractDir);

            var bundleRoot = FindBundleRoot(extractDir);
            CopyDirectory(bundleRoot.FullName, stageDir);
            _ = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(stageDir), cancellationToken).ConfigureAwait(false);

            var targetDir = GetVersionCacheDirectory(cacheRoot, packageInfo.Version.ToString());
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            Directory.Move(stageDir, targetDir);
            TouchLastUsed(targetDir);

            var installedBundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(targetDir), cancellationToken).ConfigureAwait(false);
            CleanupStaleCacheEntries(cacheRoot, packageInfo.Version.ToString());

            activity?.SetTag("aspire.skills.cache_hit", false);
            return AspireSkillsInstallResult.Installed(installedBundle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Failed to install Aspire skills bundle.");
            return AspireSkillsInstallResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
            TryDeleteDirectory(stageDir);
        }
    }

    private async Task<AspireSkillsBundle?> TryLoadCachedBundleAsync(string cacheRoot, string version, Activity? activity, CancellationToken cancellationToken)
    {
        var cacheDirectory = GetVersionCacheDirectory(cacheRoot, version);
        if (!Directory.Exists(cacheDirectory))
        {
            activity?.SetTag("aspire.skills.cache_hit", false);
            return null;
        }

        try
        {
            var bundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(cacheDirectory), cancellationToken).ConfigureAwait(false);
            TouchLastUsed(cacheDirectory);
            activity?.SetTag("aspire.skills.cache_hit", true);
            logger.LogDebug("Using cached Aspire skills bundle from {CacheDirectory}.", cacheDirectory);
            return bundle;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Ignoring invalid cached Aspire skills bundle at {CacheDirectory}.", cacheDirectory);
            return null;
        }
    }

    private string GetCacheRoot()
    {
        return Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
    }

    private static string GetVersionCacheDirectory(string cacheRoot, string version)
    {
        return Path.Combine(cacheRoot, version);
    }

    private void CleanupStaleCacheEntries(string cacheRoot, string currentVersion)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var maxAge = ReadWindow(configuration, MaxCacheAgeKey, s_defaultMaxCacheAge);
        var now = DateTimeOffset.UtcNow;

        foreach (var directory in Directory.GetDirectories(cacheRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal) || string.Equals(name, currentVersion, StringComparison.Ordinal))
            {
                continue;
            }

            var lastUsed = GetLastUsed(directory);
            if (now - lastUsed <= maxAge)
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static TimeSpan ReadWindow(IConfiguration configuration, string key, TimeSpan fallback)
    {
        if (configuration[key] is string secondsString && double.TryParse(secondsString, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return fallback;
    }

    private static void TouchLastUsed(string directory)
    {
        File.WriteAllText(Path.Combine(directory, ".lastused"), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset GetLastUsed(string directory)
    {
        var markerPath = Path.Combine(directory, ".lastused");
        if (File.Exists(markerPath) &&
            long.TryParse(File.ReadAllText(markerPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }

        return Directory.GetLastWriteTimeUtc(directory);
    }

    private static void ExtractTarball(string tarballPath, string destinationDirectory)
    {
        using var fileStream = File.OpenRead(tarballPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzipStream, destinationDirectory, overwriteFiles: true);
    }

    private static DirectoryInfo FindBundleRoot(string extractionDirectory)
    {
        var rootManifestPath = Path.Combine(extractionDirectory, "skill-manifest.json");
        if (File.Exists(rootManifestPath))
        {
            return new DirectoryInfo(extractionDirectory);
        }

        var packageDirectory = Path.Combine(extractionDirectory, "package");
        var packageManifestPath = Path.Combine(packageDirectory, "skill-manifest.json");
        if (File.Exists(packageManifestPath))
        {
            return new DirectoryInfo(packageDirectory);
        }

        throw new InvalidOperationException("Downloaded Aspire skills package does not contain skill-manifest.json.");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            var targetFileDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetFileDirectory))
            {
                Directory.CreateDirectory(targetFileDirectory);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static bool VerifyIntegrity(string filePath, string sriIntegrity)
    {
        const string prefix = "sha512-";
        if (!sriIntegrity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedHash = sriIntegrity[prefix.Length..];

        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA512.HashData(stream);
        var actualHash = Convert.ToBase64String(hashBytes);

        return string.Equals(expectedHash, actualHash, StringComparison.Ordinal);
    }

    private void TryDeleteDirectory(string directory)
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
            logger.LogDebug(ex, "Failed to delete Aspire skills cache directory {Directory}.", directory);
        }
    }
}
