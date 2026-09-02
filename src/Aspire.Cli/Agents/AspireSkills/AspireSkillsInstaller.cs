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

internal sealed class AspireSkillsInstaller(
    IGitHubArtifactAttestationVerifier githubArtifactAttestationVerifier,
    IHttpClientFactory httpClientFactory,
    IEnumerable<IAspireSkillsBundleProvider> bundleProviders,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IConfiguration configuration,
    IFeatures features,
    AspireCliTelemetry telemetry,
    ILogger<AspireSkillsInstaller> logger) : IAspireSkillsInstaller
{
    internal const string GitHubRepository = "microsoft/aspire-skills";
    internal const string ExpectedSourceRepository = $"https://github.com/{GitHubRepository}";
    internal const string ExpectedWorkflowPath = ".github/workflows/publish.yml";
    internal const string DisablePackageValidationKey = "disableAspireSkillsPackageValidation";
    internal const string VersionOverrideKey = "aspireSkillsVersion";
    internal const string MaxCacheAgeKey = "AspireSkillsMaxCacheAgeSeconds";
    internal const string ArchiveSha512FileName = ".archive-sha512";
    internal const string GitHubArchiveSha256FileName = ".github-archive-sha256";
    internal const string GitHubAttestationVerifiedFileName = ".github-attestation-verified";
    internal const string TelemetryActivityName = "AspireSkillsInstaller.Install";
    internal const string Version = "0.0.1";

    private const string ExpectedBuildType = "https://actions.github.io/buildtypes/workflow/v1";
    private const int WindowsSharingViolationHResult = unchecked((int)0x80070020);
    private const int WindowsLockViolationHResult = unchecked((int)0x80070021);
    private const int LinuxWouldBlockHResult = 11;
    private const int MacOsWouldBlockHResult = 35;
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const string LastUsedFileName = ".lastused";
    private const int CacheLockMaxAttempts = 4;

    private static readonly TimeSpan s_cacheLockInitialRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_cacheLockMaxRetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan s_defaultMaxCacheAge = TimeSpan.FromDays(7);

    private readonly IReadOnlyDictionary<AgentAssetKind, IAspireSkillsBundleProvider> _providers = CreateProviders(bundleProviders);

    public Task<AspireSkillsInstallResult> InstallAsync(
        AgentAssetKind assetKind,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(assetKind, out var provider))
        {
            return Task.FromResult(AspireSkillsInstallResult.Unavailable);
        }

        return interactionService.ShowStatusAsync(
            string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_InstallingStatus,
                provider.DisplayName),
            () => InstallCoreAsync(provider, cancellationToken));
    }

    private static IReadOnlyDictionary<AgentAssetKind, IAspireSkillsBundleProvider> CreateProviders(
        IEnumerable<IAspireSkillsBundleProvider> bundleProviders)
    {
        ArgumentNullException.ThrowIfNull(bundleProviders);

        var providersByKind = new Dictionary<AgentAssetKind, IAspireSkillsBundleProvider>();
        foreach (var provider in bundleProviders)
        {
            if (!providersByKind.TryAdd(provider.AssetKind, provider))
            {
                throw new InvalidOperationException(
                    $"Multiple Aspire Skills bundle providers are registered for asset kind '{provider.AssetKind}'.");
            }
        }

        return providersByKind;
    }

    internal static bool IsCacheLockContention(IOException exception, bool isWindows)
    {
        if (isWindows)
        {
            return exception.HResult is WindowsSharingViolationHResult or WindowsLockViolationHResult;
        }

        // On Unix, FileStream implements FileShare.None with a non-blocking flock and exposes
        // EWOULDBLOCK as the raw errno in IOException.HResult: 11 on Linux and 35 on macOS.
        // See https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs.
        return exception.HResult is LinuxWouldBlockHResult or MacOsWouldBlockHResult;
    }

    private async Task<AspireSkillsInstallResult> InstallCoreAsync(
        IAspireSkillsBundleProvider provider,
        CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartReportedActivity(TelemetryActivityName);
        activity?.SetTag(
            TelemetryConstants.Tags.AgentAssetKind,
            provider.AssetKindName);
        activity?.SetTag(
            TelemetryConstants.Tags.AgentAssetBundleName,
            provider.AssetPrefix);

        var effectiveVersion = configuration[provider.VersionOverrideKey];
        if (string.IsNullOrWhiteSpace(effectiveVersion))
        {
            effectiveVersion = AspireSkillsInstaller.Version;
        }

        activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleVersion, effectiveVersion);

        var cacheRoot = GetCacheRoot(provider);
        Directory.CreateDirectory(cacheRoot);

        var validationDisabled = string.Equals(configuration[provider.DisablePackageValidationKey], "true", StringComparison.OrdinalIgnoreCase);
        var embeddedMetadata = provider.GetEmbeddedMetadata();

        async Task<AspireSkillsInstallResult> CompleteInstallationAsync(AspireSkillsBundle bundle, string archiveSha512)
        {
            await CleanupStaleCacheEntriesAsync(
                provider,
                cacheRoot,
                effectiveVersion,
                archiveSha512,
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
        activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleRemoteFetchEnabled, remoteFetchEnabled);

        AcquisitionResult? githubResult = null;
        if (remoteFetchEnabled)
        {
            githubResult = await InstallFromGitHubAsync(provider, cacheRoot, effectiveVersion, validationDisabled, activity, cancellationToken).ConfigureAwait(false);
            if (githubResult.Status == AcquisitionStatus.Installed)
            {
                return await CompleteInstallationAsync(githubResult.Bundle!, githubResult.ArchiveSha512!).ConfigureAwait(false);
            }

            if (githubResult.Status == AcquisitionStatus.Failed)
            {
                logger.LogDebug("{BundleDisplayName} GitHub acquisition failed for version {Version}; falling back to embedded snapshot. Failure: {Failure}", provider.DisplayName, effectiveVersion, githubResult.Message);
            }
            else if (!githubResult.GitHubReleaseMetadataAvailable ||
                     githubResult.KnownGitHubArchiveSha256 is not null)
            {
                // A digest from current release metadata can select an exact cache leaf. Only
                // use an unpinned verified leaf when release metadata itself was unavailable;
                // otherwise an unidentifiable current asset could revive stale same-version content.
                var offlineCachedResult = await TryLoadCachedBundleAsync(
                    provider,
                    cacheRoot,
                    effectiveVersion,
                    expectedArchiveSha512: null,
                    expectedGitHubArchiveSha256: githubResult.KnownGitHubArchiveSha256,
                    requireVerifiedGitHubSource: true,
                    skipCompatibilityCheck: false,
                    activity,
                    cancellationToken).ConfigureAwait(false);
                if (offlineCachedResult is not null)
                {
                    logger.LogDebug(
                        "Using a previously verified GitHub {BundleDisplayName} bundle for version {Version} because GitHub is unavailable.",
                        provider.DisplayName,
                        effectiveVersion);
                    return await CompleteInstallationAsync(
                        offlineCachedResult.Bundle!,
                        offlineCachedResult.ArchiveSha512!).ConfigureAwait(false);
                }
            }
        }
        else
        {
            logger.LogDebug("{BundleDisplayName} remote fetch feature '{Feature}' is disabled; using the embedded snapshot.", provider.DisplayName, KnownFeatures.AspireSkillsRemoteFetchEnabled);
        }

        var embeddedResult = await InstallFromEmbeddedAsync(provider, cacheRoot, effectiveVersion, embeddedMetadata, activity, cancellationToken).ConfigureAwait(false);
        if (embeddedResult.Status == AcquisitionStatus.Installed)
        {
            return await CompleteInstallationAsync(embeddedResult.Bundle!, embeddedResult.ArchiveSha512!).ConfigureAwait(false);
        }

        var unavailableMessage = string.Format(
            CultureInfo.CurrentCulture,
            AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable,
            provider.DisplayName);
        var failureMessage = embeddedResult.Status == AcquisitionStatus.Failed
            ? embeddedResult.Message ?? unavailableMessage
            : githubResult is { Status: AcquisitionStatus.Failed, Message: { } githubMessage }
                ? githubMessage
                : unavailableMessage;

        activity?.SetStatus(ActivityStatusCode.Error, failureMessage);
        return AspireSkillsInstallResult.Failed(failureMessage);
    }

    private async Task<AcquisitionResult> InstallFromGitHubAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        bool validationDisabled,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        using var tempDirectory = CreateTemporaryCacheDirectory(provider, cacheRoot, "github");
        string? knownGitHubArchiveSha256 = null;
        var githubReleaseMetadataAvailable = false;

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            var release = await TryGetGitHubReleaseAsync(provider, httpClient, version, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                logger.LogDebug("{BundleDisplayName} GitHub release was unavailable for version {Version}.", provider.DisplayName, version);
                return AcquisitionResult.Unavailable();
            }

            githubReleaseMetadataAvailable = true;
            var asset = FindGitHubReleaseAsset(provider, release, version);
            if (asset is null)
            {
                logger.LogDebug("{BundleDisplayName} GitHub release {TagName} does not contain a supported bundle asset for version {Version}.", provider.DisplayName, release.TagName, version);
                return AcquisitionResult.Unavailable(githubReleaseMetadataAvailable: true);
            }

            knownGitHubArchiveSha256 = TryNormalizeArchiveSha256(asset.Digest);
            if (knownGitHubArchiveSha256 is not null)
            {
                var cachedResult = await TryLoadCachedBundleAsync(
                    provider,
                    cacheRoot,
                    version,
                    expectedArchiveSha512: null,
                    expectedGitHubArchiveSha256: knownGitHubArchiveSha256,
                    requireVerifiedGitHubSource: !validationDisabled,
                    skipCompatibilityCheck: false,
                    activity,
                    cancellationToken).ConfigureAwait(false);
                if (cachedResult is not null)
                {
                    return cachedResult;
                }
            }

            var archivePath = Path.Combine(tempDirectory.FullName, GetSafeFileName(provider, asset.Name));
            if (!await TryDownloadGitHubAssetAsync(httpClient, asset.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false))
            {
                logger.LogDebug("{BundleDisplayName} GitHub release asset {AssetName} was unavailable for version {Version}.", provider.DisplayName, asset.Name, version);
                return AcquisitionResult.Unavailable(knownGitHubArchiveSha256, githubReleaseMetadataAvailable);
            }

            try
            {
                var githubArchiveSha256 = ComputeArchiveSha256(archivePath);
                if (knownGitHubArchiveSha256 is not null &&
                    !string.Equals(githubArchiveSha256, knownGitHubArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} GitHub release asset '{1}' failed SHA-256 verification.",
                        provider.DisplayName,
                        asset.Name));
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

                var archiveSha512 = ComputeArchiveSha512(archivePath);
                var bundle = await CacheArchiveAsync(
                    provider,
                    cacheRoot,
                    archivePath,
                    version,
                    archiveSha512,
                    githubArchiveSha256,
                    validationDisabled ? BundleArchiveSource.UnverifiedGitHub : BundleArchiveSource.VerifiedGitHub,
                    cancellationToken).ConfigureAwait(false);
                activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleSource, "github");
                activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleCacheHit, false);
                return AcquisitionResult.Installed(bundle, archiveSha512, githubArchiveSha256);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                // Includes version-mismatch failures from ValidateCompatibility, which fall back to the embedded snapshot.
                logger.LogDebug(ex, "Downloaded {BundleDisplayName} GitHub release asset {AssetName} is invalid.", provider.DisplayName, asset.Name);
                return AcquisitionResult.Failed(string.Format(
                    CultureInfo.CurrentCulture,
                    AgentCommandStrings.AspireSkillsInstaller_InvalidBundle,
                    provider.DisplayName,
                    ex.Message));
            }
        }
        // HttpClient.Timeout uses an internal cancellation token, so distinguish it from caller
        // cancellation before treating the remote source as unavailable.
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "{BundleDisplayName} GitHub release acquisition timed out for version {Version}.", provider.DisplayName, version);
            return AcquisitionResult.Unavailable(knownGitHubArchiveSha256, githubReleaseMetadataAvailable);
        }
        // A truncated response body throws HttpIOException rather than HttpRequestException.
        // Catch it explicitly so local cache and archive I/O failures still propagate.
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException or JsonException)
        {
            logger.LogDebug(ex, "{BundleDisplayName} GitHub release acquisition failed for version {Version}.", provider.DisplayName, version);
            return AcquisitionResult.Unavailable(knownGitHubArchiveSha256, githubReleaseMetadataAvailable);
        }
    }

    private async Task<AcquisitionResult> InstallFromEmbeddedAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        EmbeddedAspireSkillsBundleMetadata? metadata,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (metadata is null)
        {
            logger.LogDebug("No embedded {BundleDisplayName} bundle metadata is available.", provider.DisplayName);
            return AcquisitionResult.Unavailable();
        }

        if (ValidateEmbeddedMetadata(provider, metadata) is { } metadataError)
        {
            return AcquisitionResult.Failed(
                string.Format(
                    CultureInfo.CurrentCulture,
                    AgentCommandStrings.AspireSkillsInstaller_InvalidMetadata,
                    provider.DisplayName,
                    metadataError));
        }

        if (!string.Equals(metadata.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Embedded {BundleDisplayName} bundle version {EmbeddedVersion} does not match requested version {Version}.",
                provider.DisplayName,
                metadata.Version,
                version);
            return AcquisitionResult.Unavailable();
        }

        var expectedArchiveSha512 = AspireSkillsBundleProvider.NormalizeSha512(metadata.Sha512!);
        var cachedResult = await TryLoadCachedBundleAsync(
            provider,
            cacheRoot,
            version,
            expectedArchiveSha512,
            expectedGitHubArchiveSha256: null,
            requireVerifiedGitHubSource: false,
            skipCompatibilityCheck: true,
            activity,
            cancellationToken).ConfigureAwait(false);
        if (cachedResult is not null)
        {
            return cachedResult;
        }

        using var stageDirectory = CreateTemporaryCacheDirectory(provider, cacheRoot, "stage");

        try
        {
            // The embedded snapshot ships inside the CLI binary as the trusted last-resort
            // fallback. Its `supports` range is stamped at the time the snapshot was built,
            // which can lag the actual CLI version (especially for prerelease/dogfood builds)
            // and would otherwise reject a perfectly usable local copy.
            var bundle = await provider.CreateEmbeddedBundleAsync(
                new DirectoryInfo(stageDirectory.FullName),
                cancellationToken).ConfigureAwait(false);
            if (bundle is null)
            {
                logger.LogDebug("Embedded {BundleDisplayName} archive is unavailable for version {Version}.", provider.DisplayName, version);
                return AcquisitionResult.Unavailable();
            }

            bundle = await CacheStagedBundleAsync(
                provider,
                cacheRoot,
                stageDirectory,
                bundle,
                expectedArchiveSha512,
                githubArchiveSha256: null,
                version: version,
                source: BundleArchiveSource.Embedded,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleSource, "embedded");
            activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleCacheHit, false);
            return AcquisitionResult.Installed(bundle, expectedArchiveSha512);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Embedded {BundleDisplayName} bundle {AssetName} is invalid.", provider.DisplayName, metadata.AssetName);
            return AcquisitionResult.Failed(string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_InvalidBundle,
                provider.DisplayName,
                ex.Message));
        }
    }

    private static string? ValidateEmbeddedMetadata(
        IAspireSkillsBundleProvider provider,
        EmbeddedAspireSkillsBundleMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Version))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_MissingMetadataVersion,
                provider.DisplayName);
        }

        if (!string.Equals(metadata.Repository, GitHubRepository, StringComparison.OrdinalIgnoreCase))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_MetadataRepositoryMismatch,
                provider.DisplayName,
                metadata.Repository,
                GitHubRepository);
        }

        if (string.IsNullOrWhiteSpace(metadata.Tag))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_MissingMetadataTag,
                provider.DisplayName);
        }

        if (string.IsNullOrWhiteSpace(metadata.AssetName))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_MissingMetadataAssetName,
                provider.DisplayName);
        }

        if (string.IsNullOrWhiteSpace(metadata.Sha512))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_MissingMetadataSha512,
                provider.DisplayName);
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

    private static string? TryNormalizeArchiveSha512(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var sha512 = AspireSkillsBundleProvider.NormalizeSha512(digest);
        return sha512.Length == 128 && sha512.All(Uri.IsHexDigit)
            ? sha512.ToLowerInvariant()
            : null;
    }

    private async Task<GitHubReleaseInfo?> TryGetGitHubReleaseAsync(
        IAspireSkillsBundleProvider provider,
        HttpClient httpClient,
        string version,
        CancellationToken cancellationToken)
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
                logger.LogDebug(
                    "Failed to fetch GitHub release {Tag} for the {BundleDisplayName} bundle: HTTP {StatusCode}.",
                    tag,
                    provider.DisplayName,
                    response.StatusCode);
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

    private static GitHubReleaseAsset? FindGitHubReleaseAsset(
        IAspireSkillsBundleProvider provider,
        GitHubReleaseInfo release,
        string version)
    {
        foreach (var assetName in GetGitHubReleaseAssetNameCandidates(provider, version))
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

    private static IEnumerable<string> GetGitHubReleaseAssetNameCandidates(
        IAspireSkillsBundleProvider provider,
        string version)
    {
        var unprefixedVersion = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        var prefixedVersion = $"v{unprefixedVersion}";

        foreach (var archiveExtension in new[] { ".zip", ".tar.gz", ".tgz" })
        {
            yield return $"{provider.AssetPrefix}-{prefixedVersion}{archiveExtension}";
            yield return $"{provider.AssetPrefix}-{unprefixedVersion}{archiveExtension}";
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

    private async Task<AcquisitionResult?> TryLoadCachedBundleAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        string? expectedArchiveSha512,
        string? expectedGitHubArchiveSha256,
        bool requireVerifiedGitHubSource,
        bool skipCompatibilityCheck,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleCacheHit, false);
        if (expectedArchiveSha512 is null &&
            expectedGitHubArchiveSha256 is null &&
            !requireVerifiedGitHubSource)
        {
            return null;
        }

        await using var cacheLock = await AcquireCacheLockAsync(provider, cacheRoot, version, cancellationToken).ConfigureAwait(false);
        return await TryLoadCachedBundleCoreAsync(
            provider,
            cacheRoot,
            version,
            expectedArchiveSha512,
            expectedGitHubArchiveSha256,
            requireVerifiedGitHubSource,
            skipCompatibilityCheck,
            activity,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcquisitionResult?> TryLoadCachedBundleCoreAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        string? expectedArchiveSha512,
        string? expectedGitHubArchiveSha256,
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

        if (expectedArchiveSha512 is not null)
        {
            return await TryLoadCachedBundleDirectoryAsync(
                provider,
                GetBundleCacheDirectory(versionCacheDirectory, expectedArchiveSha512),
                version,
                expectedArchiveSha512,
                expectedGitHubArchiveSha256,
                requireVerifiedGitHubSource,
                skipCompatibilityCheck,
                activity,
                cancellationToken).ConfigureAwait(false);
        }

        // GitHub release metadata exposes the attestation subject's SHA-256, while cache leaves
        // are keyed by the bundle-integrity SHA-512. Enumerate SHA-512 leaves so the persisted
        // GitHub SHA-256 mapping can select the current asset without downloading it again.
        // When release metadata is unavailable, prefer the most recently used bundle whose
        // GitHub provenance was previously verified.
        List<(string Directory, string ArchiveSha512, DateTimeOffset LastUsed)> candidates = [];
        try
        {
            foreach (var directory in Directory.GetDirectories(versionCacheDirectory))
            {
                var archiveSha512 = TryNormalizeArchiveSha512(Path.GetFileName(directory));
                if (archiveSha512 is not null)
                {
                    candidates.Add((directory, archiveSha512, GetLastUsed(directory)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to enumerate cached {BundleDisplayName} bundles for version {Version}.", provider.DisplayName, version);
            return null;
        }

        foreach (var candidate in candidates.OrderByDescending(static candidate => candidate.LastUsed))
        {
            var cachedResult = await TryLoadCachedBundleDirectoryAsync(
                provider,
                candidate.Directory,
                version,
                candidate.ArchiveSha512,
                expectedGitHubArchiveSha256,
                requireVerifiedGitHubSource,
                skipCompatibilityCheck,
                activity,
                cancellationToken).ConfigureAwait(false);
            if (cachedResult is not null)
            {
                return cachedResult;
            }
        }

        return null;
    }

    private async Task<AcquisitionResult?> TryLoadCachedBundleDirectoryAsync(
        IAspireSkillsBundleProvider provider,
        string cacheDirectory,
        string version,
        string expectedArchiveSha512,
        string? expectedGitHubArchiveSha256,
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
                "Ignoring cached {BundleDisplayName} bundle at {CacheDirectory} because GitHub attestation verification was not recorded.",
                provider.DisplayName,
                cacheDirectory);
            return null;
        }

        try
        {
            var cachedArchiveSha512Path = Path.Combine(cacheDirectory, ArchiveSha512FileName);
            var cachedArchiveSha512 = File.Exists(cachedArchiveSha512Path)
                ? TryNormalizeArchiveSha512(File.ReadAllText(cachedArchiveSha512Path).Trim())
                : null;
            if (cachedArchiveSha512 is null ||
                !string.Equals(cachedArchiveSha512, expectedArchiveSha512, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "Ignoring cached {BundleDisplayName} bundle at {CacheDirectory} because its archive SHA-512 does not match its cache identity.",
                    provider.DisplayName,
                    cacheDirectory);
                return null;
            }

            var cachedGitHubArchiveSha256Path = Path.Combine(cacheDirectory, GitHubArchiveSha256FileName);
            var cachedGitHubArchiveSha256 = File.Exists(cachedGitHubArchiveSha256Path)
                ? TryNormalizeArchiveSha256(File.ReadAllText(cachedGitHubArchiveSha256Path).Trim())
                : null;
            if (expectedGitHubArchiveSha256 is not null &&
                !string.Equals(cachedGitHubArchiveSha256, expectedGitHubArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "Ignoring cached {BundleDisplayName} bundle at {CacheDirectory} because its GitHub archive SHA-256 does not match the current release asset.",
                    provider.DisplayName,
                    cacheDirectory);
                return null;
            }

            var bundle = await provider.LoadAsync(
                new DirectoryInfo(cacheDirectory),
                cancellationToken,
                skipCompatibilityCheck).ConfigureAwait(false);
            ValidateBundleVersion(provider, bundle, version);
            TouchLastUsed(provider, cacheDirectory);
            activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleSource, "cache");
            activity?.SetTag(TelemetryConstants.Tags.AgentAssetBundleCacheHit, true);
            logger.LogDebug("Using cached {BundleDisplayName} bundle from {CacheDirectory}.", provider.DisplayName, cacheDirectory);
            return AcquisitionResult.Installed(bundle, cachedArchiveSha512, cachedGitHubArchiveSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Ignoring invalid cached {BundleDisplayName} bundle at {CacheDirectory}.", provider.DisplayName, cacheDirectory);
            return null;
        }
    }

    private async Task<AspireSkillsBundle> CacheArchiveAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string archivePath,
        string version,
        string archiveSha512,
        string? githubArchiveSha256,
        BundleArchiveSource source,
        CancellationToken cancellationToken)
    {
        using var stageDirectory = CreateTemporaryCacheDirectory(provider, cacheRoot, "stage");

        var stagedBundle = await provider.CreateAsync(
            new FileInfo(archivePath),
            new DirectoryInfo(stageDirectory.FullName),
            archiveSha512,
            cancellationToken).ConfigureAwait(false);

        return await CacheStagedBundleAsync(
            provider,
            cacheRoot,
            stageDirectory,
            stagedBundle,
            archiveSha512,
            githubArchiveSha256,
            version,
            source,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AspireSkillsBundle> CacheStagedBundleAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        TemporaryCacheDirectory stageDirectory,
        AspireSkillsBundle stagedBundle,
        string archiveSha512,
        string? githubArchiveSha256,
        string version,
        BundleArchiveSource source,
        CancellationToken cancellationToken)
    {
        RemoveInstallerMetadata(stageDirectory.FullName);
        ValidateBundleVersion(provider, stagedBundle, version);
        // The archive is discarded after extraction. Retain its digest so same-version
        // archives can be distinguished without keeping or downloading the archive again.
        await File.WriteAllTextAsync(
            Path.Combine(stageDirectory.FullName, ArchiveSha512FileName),
            AspireSkillsBundleProvider.NormalizeSha512(archiveSha512).ToLowerInvariant(),
            cancellationToken).ConfigureAwait(false);
        if (githubArchiveSha256 is not null)
        {
            // GitHub release metadata and artifact attestations currently identify subjects by
            // SHA-256. Persist that secondary identity so the next release lookup can find this
            // SHA-512-keyed leaf without downloading the archive first.
            await File.WriteAllTextAsync(
                Path.Combine(stageDirectory.FullName, GitHubArchiveSha256FileName),
                AspireSkillsBundleProvider.NormalizeSha256(githubArchiveSha256).ToLowerInvariant(),
                cancellationToken).ConfigureAwait(false);
        }

        if (source == BundleArchiveSource.VerifiedGitHub)
        {
            await File.WriteAllTextAsync(
                Path.Combine(stageDirectory.FullName, GitHubAttestationVerifiedFileName),
                string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        await using var cacheLock = await AcquireCacheLockAsync(provider, cacheRoot, version, cancellationToken).ConfigureAwait(false);
        var versionCacheDirectory = GetVersionCacheDirectory(cacheRoot, version);
        var targetDir = GetBundleCacheDirectory(versionCacheDirectory, archiveSha512);
        var cachedResult = await TryLoadCachedBundleCoreAsync(
            provider,
            cacheRoot,
            version,
            archiveSha512,
            githubArchiveSha256,
            requireVerifiedGitHubSource: source == BundleArchiveSource.VerifiedGitHub,
            skipCompatibilityCheck: source == BundleArchiveSource.Embedded,
            activity: null,
            cancellationToken).ConfigureAwait(false);
        if (cachedResult is not null)
        {
            return cachedResult.Bundle!;
        }

        if (Directory.Exists(targetDir))
        {
            logger.LogDebug("Replacing {BundleDisplayName} cache directory {CacheDirectory}.", provider.DisplayName, targetDir);
            TryDeleteDirectory(provider, targetDir);
            if (Directory.Exists(targetDir))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Could not replace {0} cache directory '{1}'.",
                    provider.DisplayName,
                    targetDir));
            }
        }

        Directory.CreateDirectory(versionCacheDirectory);
        RemoveLegacyCacheLayout(provider, versionCacheDirectory);
        stageDirectory.MoveTo(targetDir);
        TouchLastUsed(provider, targetDir);

        return stagedBundle;
    }

    private static void RemoveInstallerMetadata(string bundleDirectory)
    {
        // These files describe local installer state, not bundle content. Always recreate them
        // from the acquisition path so an archive cannot claim freshness or GitHub provenance.
        File.Delete(Path.Combine(bundleDirectory, ArchiveSha512FileName));
        File.Delete(Path.Combine(bundleDirectory, GitHubArchiveSha256FileName));
        File.Delete(Path.Combine(bundleDirectory, GitHubAttestationVerifiedFileName));
        File.Delete(Path.Combine(bundleDirectory, LastUsedFileName));
    }

    private TemporaryCacheDirectory CreateTemporaryCacheDirectory(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string prefix)
    {
        return TemporaryCacheDirectory.Create(
            cacheRoot,
            prefix,
            directory => TryDeleteDirectory(provider, directory),
            path => TryDeleteFile(provider, path));
    }

    private Task<FileStream> AcquireCacheLockAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        CancellationToken cancellationToken)
    {
        return AcquireCacheLockCoreAsync(provider, cacheRoot, version, maxAttempts: null, cancellationToken);
    }

    private Task<FileStream> AcquireCacheLockForCleanupAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        CancellationToken cancellationToken)
    {
        return AcquireCacheLockCoreAsync(provider, cacheRoot, version, CacheLockMaxAttempts, cancellationToken);
    }

    private async Task<FileStream> AcquireCacheLockCoreAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string version,
        int? maxAttempts,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(cacheRoot, $".{GetSafeFileName(provider, version)}.lock");
        var retryDelay = s_cacheLockInitialRetryDelay;
        for (var attempt = 1; ; attempt++)
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
            catch (IOException ex) when (
                IsCacheLockContention(ex, OperatingSystem.IsWindows()) &&
                (maxAttempts is null || attempt < maxAttempts.Value))
            {
                if (maxAttempts is { } boundedAttempts)
                {
                    logger.LogDebug(
                        "Acquiring the {BundleDisplayName} bundle cache lock for version {Version} failed with HRESULT {HResult}; retrying in {DelayMilliseconds} ms (retry {RetryCount} of {MaxRetries}).",
                        provider.DisplayName,
                        version,
                        ex.HResult,
                        retryDelay.TotalMilliseconds,
                        attempt,
                        boundedAttempts - 1);
                }
                else
                {
                    logger.LogDebug(
                        "Acquiring the {BundleDisplayName} bundle cache lock for version {Version} failed with HRESULT {HResult}; retrying in {DelayMilliseconds} ms.",
                        provider.DisplayName,
                        version,
                        ex.HResult,
                        retryDelay.TotalMilliseconds);
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                if (retryDelay < s_cacheLockMaxRetryDelay)
                {
                    retryDelay *= 2;
                }
            }
        }
    }

    private static void ValidateBundleVersion(
        IAspireSkillsBundleProvider provider,
        AspireSkillsBundle bundle,
        string expectedVersion)
    {
        if (!string.Equals(bundle.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} bundle version '{1}' does not match expected version '{2}'.",
                provider.DisplayName,
                bundle.Version,
                expectedVersion));
        }
    }

    private static string ComputeArchiveSha256(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeArchiveSha512(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    private string GetCacheRoot(IAspireSkillsBundleProvider provider)
    {
        return Path.Combine(executionContext.CacheDirectory.FullName, provider.CacheDirectoryName);
    }

    private static string GetVersionCacheDirectory(string cacheRoot, string version)
    {
        return Path.Combine(cacheRoot, version);
    }

    private static string GetBundleCacheDirectory(string versionCacheDirectory, string archiveSha512)
    {
        return Path.Combine(
            versionCacheDirectory,
            AspireSkillsBundleProvider.NormalizeSha512(archiveSha512).ToLowerInvariant());
    }

    private async Task CleanupStaleCacheEntriesAsync(
        IAspireSkillsBundleProvider provider,
        string cacheRoot,
        string currentVersion,
        string currentArchiveSha512,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var maxAge = ReadWindow(configuration, provider.MaxCacheAgeKey, s_defaultMaxCacheAge);
        string[] cacheDirectories;
        try
        {
            cacheDirectories = Directory.GetDirectories(cacheRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to enumerate {BundleDisplayName} cache directories for cleanup.", provider.DisplayName);
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
                        var leasePath = TemporaryCacheDirectory.GetLeasePath(directory);
                        using (TemporaryCacheDirectory.OpenLease(directory))
                        {
                            TryDeleteDirectory(provider, directory);
                        }

                        TryDeleteFile(provider, leasePath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Failed to evaluate temporary {BundleDisplayName} cache directory {Directory} for cleanup.", provider.DisplayName, directory);
                }

                continue;
            }

            try
            {
                await using var cacheLock = await AcquireCacheLockForCleanupAsync(
                    provider,
                    cacheRoot,
                    version,
                    cancellationToken).ConfigureAwait(false);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var isCurrentVersion = string.Equals(version, currentVersion, StringComparison.Ordinal);
                if (HasLegacyCacheLayout(provider, directory) &&
                    (isCurrentVersion || DateTimeOffset.UtcNow - GetLastUsed(directory) > maxAge))
                {
                    RemoveLegacyCacheLayout(provider, directory);
                }

                foreach (var bundleDirectory in Directory.GetDirectories(directory))
                {
                    var directoryName = Path.GetFileName(bundleDirectory);
                    var archiveSha512 = TryNormalizeArchiveSha512(directoryName);
                    if (archiveSha512 is null)
                    {
                        continue;
                    }

                    if ((isCurrentVersion &&
                         string.Equals(archiveSha512, currentArchiveSha512, StringComparison.OrdinalIgnoreCase)) ||
                        DateTimeOffset.UtcNow - GetLastUsed(bundleDirectory) <= maxAge)
                    {
                        continue;
                    }

                    TryDeleteDirectory(provider, bundleDirectory);
                }

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    TryDeleteDirectory(provider, directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleanup is optional. Leave an unavailable cache for a later pass after
                // lock acquisition or filesystem access exhausts its retry/error handling.
                logger.LogDebug(
                    ex,
                    "Skipping cleanup of {BundleDisplayName} cache version {Version} because it could not be evaluated.",
                    provider.DisplayName,
                    version);
            }
        }
    }

    private static bool HasLegacyCacheLayout(
        IAspireSkillsBundleProvider provider,
        string versionCacheDirectory)
    {
        return Directory.Exists(Path.Combine(versionCacheDirectory, provider.ContentRootDirectoryName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, provider.ManifestFileName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, ArchiveSha512FileName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, GitHubArchiveSha256FileName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, GitHubAttestationVerifiedFileName)) ||
            File.Exists(Path.Combine(versionCacheDirectory, LastUsedFileName));
    }

    private void RemoveLegacyCacheLayout(
        IAspireSkillsBundleProvider provider,
        string versionCacheDirectory)
    {
        // Older CLIs stored extracted files directly in the version directory. Remove only
        // those known entries so digest-addressed children created by newer CLIs remain intact.
        TryDeleteDirectory(provider, Path.Combine(versionCacheDirectory, provider.ContentRootDirectoryName));
        TryDeleteFile(provider, Path.Combine(versionCacheDirectory, provider.ManifestFileName));
        TryDeleteFile(provider, Path.Combine(versionCacheDirectory, ArchiveSha512FileName));
        TryDeleteFile(provider, Path.Combine(versionCacheDirectory, GitHubArchiveSha256FileName));
        TryDeleteFile(provider, Path.Combine(versionCacheDirectory, GitHubAttestationVerifiedFileName));
        TryDeleteFile(provider, Path.Combine(versionCacheDirectory, LastUsedFileName));
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

    private void TouchLastUsed(IAspireSkillsBundleProvider provider, string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, LastUsedFileName), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to update {BundleDisplayName} cache last-used marker for {Directory}.", provider.DisplayName, directory);
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

    private static string GetSafeFileName(
        IAspireSkillsBundleProvider provider,
        string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidCharacter, '_');
        }

        return string.IsNullOrWhiteSpace(safeName) ? $"{provider.AssetPrefix}-{Guid.NewGuid():N}.archive" : safeName;
    }

    private void TryDeleteDirectory(IAspireSkillsBundleProvider provider, string directory)
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
            logger.LogDebug(ex, "Failed to delete {BundleDisplayName} cache directory {Directory}.", provider.DisplayName, directory);
        }
    }

    private void TryDeleteFile(IAspireSkillsBundleProvider provider, string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to delete {BundleDisplayName} cache file {Path}.", provider.DisplayName, path);
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
        string? ArchiveSha512,
        string? KnownGitHubArchiveSha256,
        bool GitHubReleaseMetadataAvailable)
    {
        public static AcquisitionResult Installed(
            AspireSkillsBundle bundle,
            string archiveSha512,
            string? knownGitHubArchiveSha256 = null)
        {
            return new AcquisitionResult(
                AcquisitionStatus.Installed,
                bundle,
                null,
                archiveSha512,
                knownGitHubArchiveSha256,
                GitHubReleaseMetadataAvailable: false);
        }

        public static AcquisitionResult Unavailable(
            string? knownGitHubArchiveSha256 = null,
            bool githubReleaseMetadataAvailable = false)
        {
            return new AcquisitionResult(
                AcquisitionStatus.Unavailable,
                null,
                null,
                null,
                knownGitHubArchiveSha256,
                githubReleaseMetadataAvailable);
        }

        public static AcquisitionResult Failed(string message)
        {
            return new AcquisitionResult(
                AcquisitionStatus.Failed,
                null,
                message,
                null,
                null,
                GitHubReleaseMetadataAvailable: false);
        }
    }

    private sealed record GitHubReleaseInfo(string TagName, IReadOnlyList<GitHubReleaseAsset> Assets);

    private sealed record GitHubReleaseAsset(string Name, string DownloadUrl, string? Digest);
}
