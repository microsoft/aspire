// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Configuration;
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
    IAspireSkillsBundleProvider bundleProvider,
    IEmbeddedAspireSkillsBundleProvider embeddedBundleProvider,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IConfiguration configuration,
    IFeatures features,
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
    internal const string ArchiveSha256FileName = ".archive-sha256";
    internal const string GitHubAttestationVerifiedFileName = ".github-attestation-verified";
    private const string LastUsedFileName = ".lastused";

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

        var validationDisabled = string.Equals(configuration[DisablePackageValidationKey], "true", StringComparison.OrdinalIgnoreCase);
        var embeddedMetadata = embeddedBundleProvider.Metadata;

        async Task<AspireSkillsInstallResult> CompleteInstallationAsync(AspireSkillsBundle bundle)
        {
            await CleanupStaleCacheEntriesAsync(
                cacheRoot,
                effectiveVersion,
                bundle.ArchiveSha256,
                cancellationToken).ConfigureAwait(false);
            return AspireSkillsInstallResult.Installed(bundle);
        }

        // The remote fetch path is opt-in. Ship 13.4 with this disabled so users only
        // get the embedded snapshot (no unattended network call out to GitHub on every
        // `aspire agent init`). Toggle the feature on to opt in to the GitHub release path,
        // which still falls back to the embedded snapshot if the network call fails.
        var remoteFetchEnabled = features.IsFeatureEnabled(
            KnownFeatures.AspireSkillsRemoteFetchEnabled,
            KnownFeatures.GetFeatureMetadata(KnownFeatures.AspireSkillsRemoteFetchEnabled)!.DefaultValue);
        activity?.SetTag("aspire.skills.remote_fetch_enabled", remoteFetchEnabled);

        AcquisitionResult? githubResult = null;
        if (remoteFetchEnabled)
        {
            githubResult = await InstallFromGitHubAsync(cacheRoot, effectiveVersion, validationDisabled, activity, cancellationToken).ConfigureAwait(false);
            if (githubResult.Status == AcquisitionStatus.Installed)
            {
                return await CompleteInstallationAsync(githubResult.Bundle!).ConfigureAwait(false);
            }

            if (githubResult.Status == AcquisitionStatus.Failed)
            {
                logger.LogDebug("Aspire skills GitHub acquisition failed for version {Version}; falling back to embedded snapshot. Failure: {Failure}", effectiveVersion, githubResult.Message);
            }
            else
            {
                // Preserve any digest discovered from release metadata so a cache already
                // proven stale is not reconsidered as a generic offline fallback. If release
                // metadata itself was unavailable, prefer the last bundle verified by the
                // expected GitHub workflow over the older embedded snapshot.
                var offlineCachedBundle = await TryLoadCachedBundleAsync(
                    cacheRoot,
                    effectiveVersion,
                    githubResult.KnownArchiveSha256,
                    requireVerifiedGitHubSource: true,
                    skipCompatibilityCheck: false,
                    activity,
                    cancellationToken).ConfigureAwait(false);
                if (offlineCachedBundle is not null)
                {
                    logger.LogDebug(
                        "Using a previously verified GitHub Aspire skills bundle for version {Version} because GitHub is unavailable.",
                        effectiveVersion);
                    return await CompleteInstallationAsync(offlineCachedBundle).ConfigureAwait(false);
                }
            }
        }
        else
        {
            logger.LogDebug("Aspire skills remote fetch feature '{Feature}' is disabled; using the embedded snapshot.", KnownFeatures.AspireSkillsRemoteFetchEnabled);
        }

        var embeddedResult = await InstallFromEmbeddedAsync(cacheRoot, effectiveVersion, embeddedMetadata, activity, cancellationToken).ConfigureAwait(false);
        if (embeddedResult.Status == AcquisitionStatus.Installed)
        {
            return await CompleteInstallationAsync(embeddedResult.Bundle!).ConfigureAwait(false);
        }

        var failureMessage = embeddedResult.Status == AcquisitionStatus.Failed
            ? embeddedResult.Message ?? AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable
            : githubResult is { Status: AcquisitionStatus.Failed, Message: { } githubMessage }
                ? githubMessage
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
        using var tempDirectory = CreateTemporaryCacheDirectory(cacheRoot, "github");
        string? knownArchiveSha256 = null;

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

            knownArchiveSha256 = TryNormalizeArchiveSha256(asset.Digest);
            if (knownArchiveSha256 is not null)
            {
                var cachedBundle = await TryLoadCachedBundleAsync(
                    cacheRoot,
                    version,
                    knownArchiveSha256,
                    requireVerifiedGitHubSource: !validationDisabled,
                    skipCompatibilityCheck: false,
                    activity,
                    cancellationToken).ConfigureAwait(false);
                if (cachedBundle is not null)
                {
                    return AcquisitionResult.Installed(cachedBundle);
                }
            }

            var archivePath = Path.Combine(tempDirectory.FullName, GetSafeFileName(asset.Name));
            if (!await TryDownloadGitHubAssetAsync(httpClient, asset.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false))
            {
                logger.LogDebug("Aspire skills GitHub release asset {AssetName} was unavailable for version {Version}.", asset.Name, version);
                return AcquisitionResult.Unavailable(knownArchiveSha256);
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
                var archiveSha256 = knownArchiveSha256 is null
                    ? ComputeArchiveSha256(archivePath)
                    : AspireSkillsBundleProvider.NormalizeSha256(knownArchiveSha256);
                var bundle = await CacheArchiveAsync(
                    cacheRoot,
                    archivePath,
                    version,
                    archiveSha256,
                    validationDisabled ? BundleArchiveSource.UnverifiedGitHub : BundleArchiveSource.VerifiedGitHub,
                    cancellationToken).ConfigureAwait(false);
                activity?.SetTag("aspire.skills.source", "github");
                activity?.SetTag("aspire.skills.cache_hit", false);
                return AcquisitionResult.Installed(bundle);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                // Includes version-mismatch failures from ValidateCompatibility, which fall back to the embedded snapshot.
                logger.LogDebug(ex, "Downloaded Aspire skills GitHub release asset {AssetName} is invalid.", asset.Name);
                return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogDebug(ex, "Aspire skills GitHub release acquisition failed for version {Version}.", version);
            return AcquisitionResult.Unavailable(knownArchiveSha256);
        }
    }

    private async Task<AcquisitionResult> InstallFromEmbeddedAsync(
        string cacheRoot,
        string version,
        EmbeddedAspireSkillsBundleMetadata? metadata,
        Activity? activity,
        CancellationToken cancellationToken)
    {
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

        var expectedArchiveSha256 = AspireSkillsBundleProvider.NormalizeSha256(metadata.Sha256!);
        var cachedBundle = await TryLoadCachedBundleAsync(
            cacheRoot,
            version,
            expectedArchiveSha256,
            requireVerifiedGitHubSource: false,
            skipCompatibilityCheck: true,
            activity,
            cancellationToken).ConfigureAwait(false);
        if (cachedBundle is not null)
        {
            return AcquisitionResult.Installed(cachedBundle);
        }

        using var stageDirectory = CreateTemporaryCacheDirectory(cacheRoot, "stage");

        try
        {
            // The embedded snapshot ships inside the CLI binary as the trusted last-resort
            // fallback. Its `supports` range is stamped at the time the snapshot was built,
            // which can lag the actual CLI version (especially for prerelease/dogfood builds)
            // and would otherwise reject a perfectly usable local copy.
            var bundle = await embeddedBundleProvider.CreateBundleAsync(
                new DirectoryInfo(stageDirectory.FullName),
                cancellationToken).ConfigureAwait(false);
            if (bundle is null)
            {
                logger.LogDebug("Embedded Aspire skills archive is unavailable for version {Version}.", version);
                return AcquisitionResult.Unavailable();
            }

            bundle = await CacheStagedBundleAsync(
                cacheRoot,
                stageDirectory,
                bundle,
                version,
                BundleArchiveSource.Embedded,
                cancellationToken).ConfigureAwait(false);
            activity?.SetTag("aspire.skills.source", "embedded");
            activity?.SetTag("aspire.skills.cache_hit", false);
            return AcquisitionResult.Installed(bundle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Embedded Aspire skills bundle {AssetName} is invalid.", metadata.AssetName);
            return AcquisitionResult.Failed(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_InvalidBundle, ex.Message));
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

    private static string? TryNormalizeArchiveSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var sha256 = AspireSkillsBundleProvider.NormalizeSha256(digest);
        return sha256.Length == 64 && sha256.All(Uri.IsHexDigit)
            ? sha256.ToLowerInvariant()
            : null;
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
                    var digest = assetElement.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String
                        ? digestElement.GetString()
                        : null;
                    assets.Add(new GitHubReleaseAsset(name, downloadUrl, digest));
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

    private async Task<AspireSkillsBundle?> TryLoadCachedBundleAsync(
        string cacheRoot,
        string version,
        string? expectedArchiveSha256,
        bool requireVerifiedGitHubSource,
        bool skipCompatibilityCheck,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        activity?.SetTag("aspire.skills.cache_hit", false);
        if (expectedArchiveSha256 is null && !requireVerifiedGitHubSource)
        {
            return null;
        }

        await using var cacheLock = await AcquireCacheLockAsync(cacheRoot, version, cancellationToken).ConfigureAwait(false);
        return await TryLoadCachedBundleCoreAsync(
            cacheRoot,
            version,
            expectedArchiveSha256,
            requireVerifiedGitHubSource,
            skipCompatibilityCheck,
            activity,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AspireSkillsBundle?> TryLoadCachedBundleCoreAsync(
        string cacheRoot,
        string version,
        string? expectedArchiveSha256,
        bool requireVerifiedGitHubSource,
        bool skipCompatibilityCheck,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var versionCacheDirectory = GetVersionCacheDirectory(cacheRoot, version);
        if (!Directory.Exists(versionCacheDirectory))
        {
            return null;
        }

        if (expectedArchiveSha256 is not null)
        {
            return await TryLoadCachedBundleDirectoryAsync(
                GetBundleCacheDirectory(versionCacheDirectory, expectedArchiveSha256),
                version,
                expectedArchiveSha256,
                requireVerifiedGitHubSource,
                skipCompatibilityCheck,
                activity,
                cancellationToken).ConfigureAwait(false);
        }

        // When GitHub release metadata is unavailable, there is no current digest to select.
        // Prefer the most recently used compatible bundle whose GitHub provenance was previously
        // verified. A digest learned from metadata never reaches this path, so a known-stale leaf
        // cannot be reconsidered as an offline fallback.
        List<(string Directory, string ArchiveSha256, DateTimeOffset LastUsed)> candidates = [];
        try
        {
            foreach (var directory in Directory.GetDirectories(versionCacheDirectory))
            {
                var archiveSha256 = TryNormalizeArchiveSha256(Path.GetFileName(directory));
                if (archiveSha256 is not null)
                {
                    candidates.Add((directory, archiveSha256, GetLastUsed(directory)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to enumerate cached Aspire skills bundles for version {Version}.", version);
            return null;
        }

        foreach (var candidate in candidates.OrderByDescending(static candidate => candidate.LastUsed))
        {
            var cachedBundle = await TryLoadCachedBundleDirectoryAsync(
                candidate.Directory,
                version,
                candidate.ArchiveSha256,
                requireVerifiedGitHubSource,
                skipCompatibilityCheck,
                activity,
                cancellationToken).ConfigureAwait(false);
            if (cachedBundle is not null)
            {
                return cachedBundle;
            }
        }

        return null;
    }

    private async Task<AspireSkillsBundle?> TryLoadCachedBundleDirectoryAsync(
        string cacheDirectory,
        string version,
        string expectedArchiveSha256,
        bool requireVerifiedGitHubSource,
        bool skipCompatibilityCheck,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return null;
        }

        if (requireVerifiedGitHubSource &&
            !File.Exists(Path.Combine(cacheDirectory, GitHubAttestationVerifiedFileName)))
        {
            logger.LogDebug(
                "Ignoring cached Aspire skills bundle at {CacheDirectory} because GitHub attestation verification was not recorded.",
                cacheDirectory);
            return null;
        }

        try
        {
            var cachedArchiveSha256Path = Path.Combine(cacheDirectory, ArchiveSha256FileName);
            var cachedArchiveSha256 = File.Exists(cachedArchiveSha256Path)
                ? TryNormalizeArchiveSha256(File.ReadAllText(cachedArchiveSha256Path).Trim())
                : null;
            if (cachedArchiveSha256 is null ||
                !string.Equals(cachedArchiveSha256, expectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "Ignoring cached Aspire skills bundle at {CacheDirectory} because its archive SHA-256 does not match its cache identity.",
                    cacheDirectory);
                return null;
            }

            var bundle = await bundleProvider.LoadAsync(
                new DirectoryInfo(cacheDirectory),
                cachedArchiveSha256,
                cancellationToken,
                skipCompatibilityCheck).ConfigureAwait(false);
            ValidateBundleVersion(bundle, version);
            TouchLastUsed(cacheDirectory);
            activity?.SetTag("aspire.skills.cache_hit", true);
            logger.LogDebug("Using cached Aspire skills bundle from {CacheDirectory}.", cacheDirectory);
            return bundle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Ignoring invalid cached Aspire skills bundle at {CacheDirectory}.", cacheDirectory);
            return null;
        }
    }

    private async Task<AspireSkillsBundle> CacheArchiveAsync(
        string cacheRoot,
        string archivePath,
        string version,
        string archiveSha256,
        BundleArchiveSource source,
        CancellationToken cancellationToken)
    {
        using var stageDirectory = CreateTemporaryCacheDirectory(cacheRoot, "stage");

        var stagedBundle = await bundleProvider.CreateAsync(
            new FileInfo(archivePath),
            new DirectoryInfo(stageDirectory.FullName),
            archiveSha256,
            cancellationToken).ConfigureAwait(false);

        return await CacheStagedBundleAsync(
            cacheRoot,
            stageDirectory,
            stagedBundle,
            version,
            source,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AspireSkillsBundle> CacheStagedBundleAsync(
        string cacheRoot,
        TemporaryCacheDirectory stageDirectory,
        AspireSkillsBundle stagedBundle,
        string version,
        BundleArchiveSource source,
        CancellationToken cancellationToken)
    {
        RemoveInstallerMetadata(stageDirectory.FullName);
        ValidateBundleVersion(stagedBundle, version);
        var archiveSha256 = stagedBundle.ArchiveSha256;
        // The archive is discarded after extraction. Retain its digest so same-version
        // archives can be distinguished without keeping or downloading the archive again.
        await File.WriteAllTextAsync(
            Path.Combine(stageDirectory.FullName, ArchiveSha256FileName),
            AspireSkillsBundleProvider.NormalizeSha256(archiveSha256).ToLowerInvariant(),
            cancellationToken).ConfigureAwait(false);
        if (source == BundleArchiveSource.VerifiedGitHub)
        {
            await File.WriteAllTextAsync(
                Path.Combine(stageDirectory.FullName, GitHubAttestationVerifiedFileName),
                string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        await using var cacheLock = await AcquireCacheLockAsync(cacheRoot, version, cancellationToken).ConfigureAwait(false);
        var versionCacheDirectory = GetVersionCacheDirectory(cacheRoot, version);
        var targetDir = GetBundleCacheDirectory(versionCacheDirectory, archiveSha256);
        var cachedBundle = await TryLoadCachedBundleCoreAsync(
            cacheRoot,
            version,
            archiveSha256,
            requireVerifiedGitHubSource: source == BundleArchiveSource.VerifiedGitHub,
            skipCompatibilityCheck: source == BundleArchiveSource.Embedded,
            activity: null,
            cancellationToken).ConfigureAwait(false);
        if (cachedBundle is not null)
        {
            return cachedBundle;
        }

        if (Directory.Exists(targetDir))
        {
            logger.LogDebug("Replacing Aspire skills cache directory {CacheDirectory}.", targetDir);
            TryDeleteDirectory(targetDir);
            if (Directory.Exists(targetDir))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Could not replace Aspire skills cache directory '{0}'.", targetDir));
            }
        }

        Directory.CreateDirectory(versionCacheDirectory);
        RemoveLegacyCacheLayout(versionCacheDirectory);
        stageDirectory.MoveTo(targetDir);
        TouchLastUsed(targetDir);

        return stagedBundle;
    }

    private static void RemoveInstallerMetadata(string bundleDirectory)
    {
        // These files describe local installer state, not bundle content. Always recreate them
        // from the acquisition path so an archive cannot claim freshness or GitHub provenance.
        File.Delete(Path.Combine(bundleDirectory, ArchiveSha256FileName));
        File.Delete(Path.Combine(bundleDirectory, GitHubAttestationVerifiedFileName));
        File.Delete(Path.Combine(bundleDirectory, LastUsedFileName));
    }

    private TemporaryCacheDirectory CreateTemporaryCacheDirectory(string cacheRoot, string prefix)
    {
        var fullName = Path.Combine(cacheRoot, $".{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fullName);
        var leasePath = GetTemporaryCacheDirectoryLeasePath(fullName);
        return new TemporaryCacheDirectory(
            fullName,
            leasePath,
            OpenTemporaryCacheDirectoryLease(fullName),
            TryDeleteDirectory,
            TryDeleteFile);
    }

    private static FileStream OpenTemporaryCacheDirectoryLease(string directory)
    {
        return new FileStream(
            GetTemporaryCacheDirectoryLeasePath(directory),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
    }

    private static string GetTemporaryCacheDirectoryLeasePath(string directory)
    {
        return $"{directory}.lock";
    }

    private static async Task<FileStream> AcquireCacheLockAsync(string cacheRoot, string version, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(cacheRoot, $".{GetSafeFileName(version)}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the path visible for the lifetime of the cache. Unlinking a held lock on Unix
                // would let another process create a different inode and enter the same critical section.
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private static string ComputeArchiveSha256(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private string GetCacheRoot()
    {
        return Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
    }

    private static string GetVersionCacheDirectory(string cacheRoot, string version)
    {
        return Path.Combine(cacheRoot, version);
    }

    private static string GetBundleCacheDirectory(string versionCacheDirectory, string archiveSha256)
    {
        return Path.Combine(
            versionCacheDirectory,
            AspireSkillsBundleProvider.NormalizeSha256(archiveSha256).ToLowerInvariant());
    }

    private async Task CleanupStaleCacheEntriesAsync(
        string cacheRoot,
        string currentVersion,
        string currentArchiveSha256,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var maxAge = ReadWindow(configuration, MaxCacheAgeKey, s_defaultMaxCacheAge);
        string[] cacheDirectories;
        try
        {
            cacheDirectories = Directory.GetDirectories(cacheRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to enumerate Aspire skills cache directories for cleanup.");
            return;
        }

        foreach (var directory in cacheDirectories)
        {
            var version = Path.GetFileName(directory);
            if (version.StartsWith(".", StringComparison.Ordinal))
            {
                try
                {
                    if (IsTemporaryCacheDirectory(version) &&
                        DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > maxAge)
                    {
                        var leasePath = GetTemporaryCacheDirectoryLeasePath(directory);
                        using (OpenTemporaryCacheDirectoryLease(directory))
                        {
                            TryDeleteDirectory(directory);
                        }

                        TryDeleteFile(leasePath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Failed to evaluate temporary Aspire skills cache directory {Directory} for cleanup.", directory);
                }

                continue;
            }

            try
            {
                await using var cacheLock = await AcquireCacheLockAsync(cacheRoot, version, cancellationToken).ConfigureAwait(false);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var isCurrentVersion = string.Equals(version, currentVersion, StringComparison.Ordinal);
                if (HasLegacyCacheLayout(directory) &&
                    (isCurrentVersion || DateTimeOffset.UtcNow - GetLastUsed(directory) > maxAge))
                {
                    RemoveLegacyCacheLayout(directory);
                }

                foreach (var bundleDirectory in Directory.GetDirectories(directory))
                {
                    var archiveSha256 = TryNormalizeArchiveSha256(Path.GetFileName(bundleDirectory));
                    if (archiveSha256 is null ||
                        (isCurrentVersion &&
                         string.Equals(archiveSha256, currentArchiveSha256, StringComparison.OrdinalIgnoreCase)) ||
                        DateTimeOffset.UtcNow - GetLastUsed(bundleDirectory) <= maxAge)
                    {
                        continue;
                    }

                    TryDeleteDirectory(bundleDirectory);
                }

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    TryDeleteDirectory(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed to evaluate Aspire skills cache directory {Directory} for cleanup.", directory);
            }
        }
    }

    private static bool HasLegacyCacheLayout(string versionCacheDirectory)
    {
        return Directory.Exists(Path.Combine(versionCacheDirectory, "skills")) ||
            File.Exists(Path.Combine(versionCacheDirectory, "skill-manifest.json")) ||
            File.Exists(Path.Combine(versionCacheDirectory, ArchiveSha256FileName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, GitHubAttestationVerifiedFileName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, LastUsedFileName));
    }

    private void RemoveLegacyCacheLayout(string versionCacheDirectory)
    {
        // Older CLIs stored extracted files directly in the version directory. Remove only
        // those known entries so digest-addressed children created by newer CLIs remain intact.
        TryDeleteDirectory(Path.Combine(versionCacheDirectory, "skills"));
        TryDeleteFile(Path.Combine(versionCacheDirectory, "skill-manifest.json"));
        TryDeleteFile(Path.Combine(versionCacheDirectory, ArchiveSha256FileName));
        TryDeleteFile(Path.Combine(versionCacheDirectory, GitHubAttestationVerifiedFileName));
        TryDeleteFile(Path.Combine(versionCacheDirectory, LastUsedFileName));
    }

    private static bool IsTemporaryCacheDirectory(string name)
    {
        return name.StartsWith(".github-", StringComparison.Ordinal) ||
            name.StartsWith(".embedded-", StringComparison.Ordinal) ||
            name.StartsWith(".extract-", StringComparison.Ordinal) ||
            name.StartsWith(".stage-", StringComparison.Ordinal);
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
            File.WriteAllText(Path.Combine(directory, LastUsedFileName), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to update Aspire skills cache last-used marker for {Directory}.", directory);
        }
    }

    private static DateTimeOffset GetLastUsed(string directory)
    {
        var markerPath = Path.Combine(directory, LastUsedFileName);
        if (File.Exists(markerPath) &&
            long.TryParse(File.ReadAllText(markerPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime) &&
            unixTime >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            unixTime <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }

        return Directory.GetLastWriteTimeUtc(directory);
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

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to delete Aspire skills cache file {Path}.", path);
        }
    }

    private enum BundleArchiveSource
    {
        Embedded,
        VerifiedGitHub,
        UnverifiedGitHub
    }

    private enum AcquisitionStatus
    {
        Installed,
        Unavailable,
        Failed
    }

    private sealed record AcquisitionResult(
        AcquisitionStatus Status,
        AspireSkillsBundle? Bundle,
        string? Message,
        string? KnownArchiveSha256)
    {
        public static AcquisitionResult Installed(AspireSkillsBundle bundle)
        {
            return new AcquisitionResult(AcquisitionStatus.Installed, bundle, null, bundle.ArchiveSha256);
        }

        public static AcquisitionResult Unavailable(string? knownArchiveSha256 = null)
        {
            return new AcquisitionResult(AcquisitionStatus.Unavailable, null, null, knownArchiveSha256);
        }

        public static AcquisitionResult Failed(string message)
        {
            return new AcquisitionResult(AcquisitionStatus.Failed, null, message, null);
        }
    }

    private sealed record GitHubReleaseInfo(string TagName, IReadOnlyList<GitHubReleaseAsset> Assets);

    private sealed record GitHubReleaseAsset(string Name, string DownloadUrl, string? Digest);

    private sealed class TemporaryCacheDirectory : IDisposable
    {
        private readonly string _leasePath;
        private readonly FileStream _lease;
        private readonly Action<string> _deleteDirectory;
        private readonly Action<string> _deleteFile;
        private bool _deleteOnDispose = true;
        private bool _disposed;

        public TemporaryCacheDirectory(
            string fullName,
            string leasePath,
            FileStream lease,
            Action<string> deleteDirectory,
            Action<string> deleteFile)
        {
            FullName = fullName;
            _leasePath = leasePath;
            _lease = lease;
            _deleteDirectory = deleteDirectory;
            _deleteFile = deleteFile;
        }

        public string FullName { get; }

        public void MoveTo(string targetDirectory)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.Move(FullName, targetDirectory);
            _deleteOnDispose = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_deleteOnDispose)
            {
                _deleteDirectory(FullName);
            }

            _lease.Dispose();
            _deleteFile(_leasePath);
        }
    }
}
