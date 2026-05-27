// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Resolves, verifies, and caches Aspire workflow skills from the external Aspire skills package.
/// </summary>
internal sealed class AspireSkillsInstaller(
    IGitHubArtifactAttestationVerifier githubArtifactAttestationVerifier,
    IHttpClientFactory httpClientFactory,
    IEmbeddedAspireSkillsBundleProvider embeddedBundleProvider,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IConfiguration configuration,
    AspireCliTelemetry telemetry,
    ILogger<AspireSkillsInstaller> logger) : IAspireSkillsInstaller
{
    internal const string Version = "0.0.1";
    internal const string GitHubRepository = "microsoft/aspire-skills";
    internal const string ExpectedSourceRepository = $"https://github.com/{GitHubRepository}";
    internal const string ExpectedWorkflowPath = ".github/workflows/publish.yml";
    internal const string ExpectedBuildType = "https://actions.github.io/buildtypes/workflow/v1";
    internal const string DisablePackageValidationKey = "disableAspireSkillsPackageValidation";
    internal const string VersionOverrideKey = "aspireSkillsVersion";
    internal const string MaxCacheAgeKey = "AspireSkillsMaxCacheAgeSeconds";

    private const string GitHubApiBaseUrl = "https://api.github.com";

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

        var githubResult = await InstallFromGitHubAsync(cacheRoot, effectiveVersion, validationDisabled, activity, cancellationToken).ConfigureAwait(false);
        if (githubResult.Status == AcquisitionStatus.Installed)
        {
            CleanupStaleCacheEntries(cacheRoot, effectiveVersion);
            return AspireSkillsInstallResult.Installed(githubResult.Bundle!);
        }

        if (githubResult.Status == AcquisitionStatus.Failed)
        {
            logger.LogDebug("Aspire skills GitHub acquisition failed for version {Version}; falling back to embedded snapshot. Failure: {Failure}", effectiveVersion, githubResult.Message);
        }

        var embeddedResult = await InstallFromEmbeddedAsync(cacheRoot, effectiveVersion, activity, cancellationToken).ConfigureAwait(false);
        if (embeddedResult.Status == AcquisitionStatus.Installed)
        {
            CleanupStaleCacheEntries(cacheRoot, effectiveVersion);
            return AspireSkillsInstallResult.Installed(embeddedResult.Bundle!);
        }

        var failureMessage = embeddedResult.Status == AcquisitionStatus.Failed
            ? embeddedResult.Message ?? AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable
            : githubResult.Status == AcquisitionStatus.Failed
                ? githubResult.Message ?? AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable
                : AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable;

        activity?.SetStatus(ActivityStatusCode.Error, failureMessage);
        return AspireSkillsInstallResult.Failed(failureMessage);
    }

    private async Task<AcquisitionResult> InstallFromGitHubAsync(
        string cacheRoot,
        string version,
        bool validationDisabled,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(cacheRoot, $".github-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            var release = await TryGetGitHubReleaseAsync(httpClient, version, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                logger.LogDebug("Aspire skills GitHub release was unavailable for version {Version}.", version);
                return AcquisitionResult.Unavailable();
            }

            var asset = FindGitHubReleaseAsset(release, version);
            if (asset is null)
            {
                logger.LogDebug("Aspire skills GitHub release {TagName} does not contain a supported bundle asset for version {Version}.", release.TagName, version);
                return AcquisitionResult.Unavailable();
            }

            var archivePath = Path.Combine(tempDir, GetSafeFileName(asset.Name));
            if (!await TryDownloadGitHubAssetAsync(httpClient, asset.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false))
            {
                logger.LogDebug("Aspire skills GitHub release asset {AssetName} was unavailable for version {Version}.", asset.Name, version);
                return AcquisitionResult.Unavailable();
            }

            if (!validationDisabled)
            {
                var provenanceResult = await githubArtifactAttestationVerifier.VerifyAsync(
                    GitHubRepository,
                    archivePath,
                    ExpectedSourceRepository,
                    ExpectedWorkflowPath,
                    ExpectedBuildType,
                    version,
                    cancellationToken).ConfigureAwait(false);

                if (!provenanceResult.IsVerified)
                {
                    return AcquisitionResult.Failed(string.Format(
                        CultureInfo.CurrentCulture,
                        AgentCommandStrings.PlaywrightCliInstaller_ProvenanceVerificationFailed,
                        $"GitHub release asset '{asset.Name}'",
                        provenanceResult.Outcome));
                }
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
                logger.LogWarning(ex, "Downloaded Aspire skills GitHub release asset {AssetName} is invalid.", asset.Name);
                return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogDebug(ex, "Aspire skills GitHub release acquisition failed for version {Version}.", version);
            return AcquisitionResult.Unavailable();
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private async Task<AcquisitionResult> InstallFromEmbeddedAsync(
        string cacheRoot,
        string version,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var metadata = embeddedBundleProvider.Metadata;
        if (metadata is null)
        {
            logger.LogDebug("No embedded Aspire skills bundle metadata is available.");
            return AcquisitionResult.Unavailable();
        }

        if (ValidateEmbeddedMetadata(metadata) is { } metadataError)
        {
            return AcquisitionResult.Failed(string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_InvalidMetadata,
                metadataError));
        }

        if (!string.Equals(metadata.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Embedded Aspire skills bundle version {EmbeddedVersion} does not match requested version {Version}.",
                metadata.Version,
                version);
            return AcquisitionResult.Unavailable();
        }

        var tempDir = Path.Combine(cacheRoot, $".embedded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var archivePath = Path.Combine(tempDir, GetSafeFileName(metadata.AssetName!));
            var archiveStream = embeddedBundleProvider.OpenArchive();
            if (archiveStream is null)
            {
                logger.LogDebug("Embedded Aspire skills archive is unavailable for version {Version}.", version);
                return AcquisitionResult.Unavailable();
            }

            await using (archiveStream)
            {
                await using var fileStream = File.Create(archivePath);
                await archiveStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            ValidateArchiveSha256(archivePath, metadata.Sha256!);

            try
            {
                var bundle = await CacheArchiveAsync(cacheRoot, archivePath, version, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("aspire.skills.source", "embedded");
                activity?.SetTag("aspire.skills.cache_hit", false);
                return AcquisitionResult.Installed(bundle);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Embedded Aspire skills bundle {AssetName} is invalid.", metadata.AssetName);
                return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Embedded Aspire skills bundle could not be staged for version {Version}.", version);
            return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static string? ValidateEmbeddedMetadata(EmbeddedAspireSkillsBundleMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Version))
        {
            return AgentCommandStrings.AspireSkillsInstaller_MissingMetadataVersion;
        }

        if (!string.Equals(metadata.Repository, GitHubRepository, StringComparison.OrdinalIgnoreCase))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_MetadataRepositoryMismatch,
                metadata.Repository,
                GitHubRepository);
        }

        if (string.IsNullOrWhiteSpace(metadata.Tag))
        {
            return AgentCommandStrings.AspireSkillsInstaller_MissingMetadataTag;
        }

        if (string.IsNullOrWhiteSpace(metadata.AssetName))
        {
            return AgentCommandStrings.AspireSkillsInstaller_MissingMetadataAssetName;
        }

        if (string.IsNullOrWhiteSpace(metadata.Sha256))
        {
            return AgentCommandStrings.AspireSkillsInstaller_MissingMetadataSha256;
        }

        return null;
    }

    private static void ValidateArchiveSha256(string archivePath, string expectedSha256)
    {
        var expectedHash = AspireSkillsBundle.NormalizeSha256(expectedSha256);
        string actualHash;
        using (var stream = File.OpenRead(archivePath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_ArchiveHashVerificationFailed,
                expectedHash,
                actualHash));
        }
    }

    private async Task<GitHubReleaseInfo?> TryGetGitHubReleaseAsync(HttpClient httpClient, string version, CancellationToken cancellationToken)
    {
        foreach (var tag in GetGitHubTagCandidates(version))
        {
            var releaseUrl = $"{GitHubApiBaseUrl}/repos/{GitHubRepository}/releases/tags/{Uri.EscapeDataString(tag)}";
            using var request = CreateGitHubRequest(releaseUrl);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Failed to fetch Aspire skills GitHub release {Tag}: HTTP {StatusCode}.", tag, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseGitHubReleaseInfo(json);
        }

        return null;
    }

    private static GitHubReleaseInfo ParseGitHubReleaseInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagNameElement) && tagNameElement.ValueKind == JsonValueKind.String
            ? tagNameElement.GetString() ?? string.Empty
            : string.Empty;

        List<GitHubReleaseAsset> assets = [];
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsElement.EnumerateArray())
            {
                if (!assetElement.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    !assetElement.TryGetProperty("browser_download_url", out var downloadUrlElement) ||
                    downloadUrlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameElement.GetString();
                var downloadUrl = downloadUrlElement.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUrl))
                {
                    assets.Add(new GitHubReleaseAsset(name, downloadUrl));
                }
            }
        }

        return new GitHubReleaseInfo(tagName, assets);
    }

    private static GitHubReleaseAsset? FindGitHubReleaseAsset(GitHubReleaseInfo release, string version)
    {
        foreach (var assetName in GetGitHubReleaseAssetNameCandidates(version))
        {
            var asset = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset is not null)
            {
                return asset;
            }
        }

        return null;
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

    private static IEnumerable<string> GetGitHubReleaseAssetNameCandidates(string version)
    {
        var unprefixedVersion = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        var prefixedVersion = $"v{unprefixedVersion}";

        foreach (var archiveExtension in new[] { ".zip", ".tar.gz", ".tgz" })
        {
            yield return $"aspire-skills-{prefixedVersion}{archiveExtension}";
            yield return $"aspire-skills-{unprefixedVersion}{archiveExtension}";
        }
    }

    private static async Task<bool> TryDownloadGitHubAssetAsync(HttpClient httpClient, string downloadUrl, string archivePath, CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(downloadUrl);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var fileStream = File.Create(archivePath);
        await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static HttpRequestMessage CreateGitHubRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("aspire-cli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
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
            ExtractArchive(archivePath, extractDir);

            var bundleRoot = FindBundleRoot(extractDir);
            CopyDirectory(bundleRoot.FullName, stageDir);

            var stagedBundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(stageDir), cancellationToken).ConfigureAwait(false);
            ValidateBundleVersion(stagedBundle, version);

            await using var cacheLock = await AcquireCacheLockAsync(cacheRoot, version, cancellationToken).ConfigureAwait(false);
            var targetDir = GetVersionCacheDirectory(cacheRoot, version);
            if (Directory.Exists(targetDir))
            {
                try
                {
                    var existingBundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(targetDir), cancellationToken).ConfigureAwait(false);
                    ValidateBundleVersion(existingBundle, version);
                    TouchLastUsed(targetDir);
                    return existingBundle;
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogDebug(ex, "Replacing invalid Aspire skills cache directory {CacheDirectory}.", targetDir);
                    TryDeleteDirectory(targetDir);
                    if (Directory.Exists(targetDir))
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Could not replace invalid Aspire skills cache directory '{0}'.", targetDir), ex);
                    }
                }
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

    private static async Task<FileStream> AcquireCacheLockAsync(string cacheRoot, string version, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(cacheRoot, $".{GetSafeFileName(version)}.lock");
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
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
            try
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed to evaluate Aspire skills cache directory {Directory} for cleanup.", directory);
            }
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

    private void TouchLastUsed(string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, ".lastused"), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to update Aspire skills cache last-used marker for {Directory}.", directory);
        }
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

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZipArchive(archivePath, destinationDirectory);
            return;
        }

        ExtractTarball(archivePath, destinationDirectory);
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

            var destinationPath = GetSafeArchiveDestinationPath(destinationRoot, entry.Name);

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

                    entry.ExtractToFile(destinationPath, overwrite: false);
                    break;

                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;

                default:
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' has unsupported type '{1}'.", entry.Name, entry.EntryType));
            }
        }
    }

    private static void ExtractZipArchive(string archivePath, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var destinationPath = GetSafeArchiveDestinationPath(destinationRoot, entry.FullName);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var destinationFileDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationFileDirectory))
            {
                Directory.CreateDirectory(destinationFileDirectory);
            }

            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    private static string GetSafeArchiveDestinationPath(string destinationRoot, string entryName)
    {
        var normalizedEntryName = entryName.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedEntryName) ||
            normalizedEntryName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' is not safe.", entryName));
        }

        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntryName.Replace('/', Path.DirectorySeparatorChar)));
        if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(destinationPath, destinationRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' escapes the extraction directory.", entryName));
        }

        return destinationPath;
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

    private static string GetSafeFileName(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidCharacter, '_');
        }

        return string.IsNullOrWhiteSpace(safeName) ? $"aspire-skills-{Guid.NewGuid():N}.archive" : safeName;
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

    private sealed record GitHubReleaseInfo(string TagName, IReadOnlyList<GitHubReleaseAsset> Assets);

    private sealed record GitHubReleaseAsset(string Name, string DownloadUrl);

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
