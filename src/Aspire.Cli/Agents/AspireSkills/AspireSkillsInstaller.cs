// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
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
    IHttpClientFactory httpClientFactory,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IConfiguration configuration,
    AspireCliTelemetry telemetry,
    ILogger<AspireSkillsInstaller> logger) : IAspireSkillsInstaller
{
    internal const string PackageName = "@microsoft/aspire-skills";
    internal const string Version = "0.0.1";
    internal const string GitHubRepository = "microsoft/aspire-skills";
    internal const string ExpectedSourceRepository = $"https://github.com/{GitHubRepository}";
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

        var validationDisabled = string.Equals(configuration[DisablePackageValidationKey], "true", StringComparison.OrdinalIgnoreCase);

        var githubResult = await InstallFromGitHubAsync(cacheRoot, effectiveVersion, activity, cancellationToken).ConfigureAwait(false);
        if (githubResult.Status == AcquisitionStatus.Installed)
        {
            CleanupStaleCacheEntries(cacheRoot, effectiveVersion);
            return AspireSkillsInstallResult.Installed(githubResult.Bundle!);
        }

        if (githubResult.Status == AcquisitionStatus.Failed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, githubResult.Message);
            return AspireSkillsInstallResult.Failed(githubResult.Message ?? AgentCommandStrings.AspireSkillsInstaller_InvalidBundle);
        }

        if (!npmRunner.IsAvailable)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "GitHub acquisition is unavailable and npm is unavailable.");
            return AspireSkillsInstallResult.Failed(AgentCommandStrings.AspireSkillsInstaller_NpmUnavailable);
        }

        var npmResult = await InstallFromNpmAsync(cacheRoot, effectiveVersion, validationDisabled, activity, cancellationToken).ConfigureAwait(false);
        if (npmResult.Status == AcquisitionStatus.Installed)
        {
            CleanupStaleCacheEntries(cacheRoot, npmResult.Bundle!.Version);
            return AspireSkillsInstallResult.Installed(npmResult.Bundle);
        }

        activity?.SetStatus(ActivityStatusCode.Error, npmResult.Message);
        return AspireSkillsInstallResult.Failed(npmResult.Message ?? string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToResolvePackage, NpmPackageInfo.FormatPackageSpecifier(PackageName, effectiveVersion)));
    }

    private async Task<AcquisitionResult> InstallFromGitHubAsync(
        string cacheRoot,
        string version,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(cacheRoot, $".github-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var tag in GetGitHubTagCandidates(version))
            {
                var archiveUrl = GetGitHubTagArchiveUrl(tag);
                var archivePath = Path.Combine(tempDir, $"{GetSafeFileName(tag)}.tar.gz");
                var downloaded = await TryDownloadGitHubArchiveAsync(archiveUrl, archivePath, cancellationToken).ConfigureAwait(false);
                if (!downloaded)
                {
                    continue;
                }

                try
                {
                    var bundle = await CacheArchiveAsync(cacheRoot, archivePath, version, cancellationToken).ConfigureAwait(false);
                    activity?.SetTag("aspire.skills.source", "github");
                    activity?.SetTag("aspire.skills.cache_hit", false);
                    return AcquisitionResult.Installed(bundle);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Downloaded Aspire skills archive from {ArchiveUrl} is invalid.", archiveUrl);
                    return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
                }
            }

            logger.LogDebug("Aspire skills GitHub archive was unavailable for version {Version}.", version);
            return AcquisitionResult.Unavailable();
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Aspire skills GitHub archive download failed for version {Version}. Falling back to npm if available.", version);
            return AcquisitionResult.Unavailable();
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private async Task<AcquisitionResult> InstallFromNpmAsync(
        string cacheRoot,
        string version,
        bool validationDisabled,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var packageInfo = await npmRunner.ResolvePackageAsync(PackageName, version, cancellationToken).ConfigureAwait(false);
        if (packageInfo is null)
        {
            return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToResolvePackage, NpmPackageInfo.FormatPackageSpecifier(PackageName, version)));
        }

        var packageSpecifier = NpmPackageInfo.FormatPackageSpecifier(PackageName, packageInfo.Version);
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
                return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_ProvenanceVerificationFailed, packageSpecifier, provenanceResult.Outcome));
            }
        }

        var tempDir = Path.Combine(cacheRoot, $".npm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var tarballPath = await npmRunner.PackAsync(PackageName, packageInfo.Version.ToString(), tempDir, cancellationToken).ConfigureAwait(false);
            if (tarballPath is null)
            {
                return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_FailedToDownload, packageSpecifier));
            }

            if (!validationDisabled && !VerifyIntegrity(tarballPath, packageInfo.Integrity))
            {
                return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.PlaywrightCliInstaller_IntegrityVerificationFailed, packageSpecifier));
            }

            var bundle = await CacheArchiveAsync(cacheRoot, tarballPath, packageInfo.Version.ToString(), cancellationToken).ConfigureAwait(false);
            activity?.SetTag("aspire.skills.source", "npm");
            activity?.SetTag("aspire.skills.cache_hit", false);
            return AcquisitionResult.Installed(bundle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to install Aspire skills bundle.");
            return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
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
            ValidateBundleVersion(bundle, version);
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

    private async Task<AspireSkillsBundle> CacheArchiveAsync(
        string cacheRoot,
        string archivePath,
        string version,
        CancellationToken cancellationToken)
    {
        var extractDir = Path.Combine(cacheRoot, $".extract-{Guid.NewGuid():N}");
        var stageDir = Path.Combine(cacheRoot, $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            ExtractTarball(archivePath, extractDir);

            var bundleRoot = FindBundleRoot(extractDir);
            CopyDirectory(bundleRoot.FullName, stageDir);

            var stagedBundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(stageDir), cancellationToken).ConfigureAwait(false);
            ValidateBundleVersion(stagedBundle, version);

            var targetDir = GetVersionCacheDirectory(cacheRoot, version);
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }

            Directory.Move(stageDir, targetDir);
            TouchLastUsed(targetDir);

            var installedBundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(targetDir), cancellationToken).ConfigureAwait(false);
            ValidateBundleVersion(installedBundle, version);

            return installedBundle;
        }
        finally
        {
            TryDeleteDirectory(extractDir);
            TryDeleteDirectory(stageDir);
        }
    }

    private async Task<bool> TryDownloadGitHubArchiveAsync(string archiveUrl, string archivePath, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient(nameof(AspireSkillsInstaller));
        using var request = new HttpRequestMessage(HttpMethod.Get, archiveUrl);
        request.Headers.UserAgent.ParseAdd("Aspire-Cli");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Aspire skills GitHub archive request to {ArchiveUrl} returned HTTP {StatusCode}.", archiveUrl, response.StatusCode);
            return false;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = File.Create(archivePath);
        await responseStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static string GetGitHubTagArchiveUrl(string tag)
    {
        return $"https://github.com/{GitHubRepository}/archive/refs/tags/{Uri.EscapeDataString(tag)}.tar.gz";
    }

    private static IEnumerable<string> GetGitHubTagCandidates(string version)
    {
        yield return $"v{version}";

        if (!version.StartsWith('v'))
        {
            yield return version;
        }
    }

    private static string GetSafeFileName(string value)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '-');
        }

        return value;
    }

    private static void ValidateBundleVersion(AspireSkillsBundle bundle, string expectedVersion)
    {
        if (!string.Equals(bundle.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle version '{0}' does not match expected version '{1}'.",
                bundle.Version,
                expectedVersion));
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
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var fileStream = File.OpenRead(tarballPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry() is { } entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var entryName = entry.Name.Replace('\\', '/');
            if (Path.IsPathRooted(entryName) ||
                entryName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' is not safe.", entry.Name));
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entryName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(destinationPath, destinationRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' escapes the extraction directory.", entry.Name));
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destinationPath);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    var destinationFileDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationFileDirectory))
                    {
                        Directory.CreateDirectory(destinationFileDirectory);
                    }

                    entry.ExtractToFile(destinationPath, overwrite: true);
                    break;

                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;

                default:
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' has unsupported type '{1}'.", entry.Name, entry.EntryType));
            }
        }
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

        var topLevelBundleDirectories = Directory
            .EnumerateDirectories(extractionDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "skill-manifest.json")))
            .ToArray();

        if (topLevelBundleDirectories.Length == 1)
        {
            return new DirectoryInfo(topLevelBundleDirectories[0]);
        }

        if (topLevelBundleDirectories.Length > 1)
        {
            throw new InvalidOperationException("Downloaded Aspire skills package contains multiple skill-manifest.json files.");
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

    private enum AcquisitionStatus
    {
        Installed,
        Unavailable,
        Failed
    }

    private sealed record AcquisitionResult(AcquisitionStatus Status, AspireSkillsBundle? Bundle, string? Message)
    {
        public static AcquisitionResult Installed(AspireSkillsBundle bundle)
        {
            return new AcquisitionResult(AcquisitionStatus.Installed, bundle, null);
        }

        public static AcquisitionResult Unavailable()
        {
            return new AcquisitionResult(AcquisitionStatus.Unavailable, null, null);
        }

        public static AcquisitionResult Failed(string message)
        {
            return new AcquisitionResult(AcquisitionStatus.Failed, null, message);
        }
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
