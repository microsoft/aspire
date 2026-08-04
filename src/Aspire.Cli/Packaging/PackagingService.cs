// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Packaging;

internal interface IPackagingService
{
    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default, string? requestedChannelName = null);

    /// <summary>
    /// Returns a user-facing reason explaining why the <c>staging</c> package channel cannot be
    /// synthesized for the running CLI, or <see langword="null"/> when staging IS available.
    /// </summary>
    /// <remarks>
    /// On a CLI whose baked <c>AspireCliChannel</c> identity is <c>daily</c>, <c>local</c>, or
    /// <c>pr-&lt;N&gt;</c>, there is no deterministic way to produce a real staging feed:
    /// those identities are not officially published release-branch builds, so no SHA-specific
    /// darc feed (<c>darc-pub-microsoft-aspire-&lt;hash&gt;</c>) carries their packages, and
    /// falling back to the shared daily feed silently resolves daily packages instead of staging
    /// ones. To avoid that downgrade
    /// (see <see href="https://github.com/microsoft/aspire/issues/16652"/>), the service refuses
    /// to fabricate a staging channel from those identities unless the caller has set
    /// <c>overrideStagingFeed</c> or enabled the staging feature flag.
    /// </remarks>
    string? GetStagingChannelUnavailableReason();
}

internal class PackagingService : IPackagingService
{
    // Configuration key used to override the staging feed URL. When non-empty,
    // PackagingService treats staging as available regardless of the CLI's
    // identity channel (see IsStagingChannelSynthesisAllowed). Surfaced from
    // tests via InternalsVisibleTo so a single literal change can't drift.
    internal const string OverrideStagingFeedConfigKey = "overrideStagingFeed";

    // Diagnostic overrides for validating staging FEED ROUTING from a locally built CLI without
    // having to produce a real official staging build. They are intentionally scoped to the
    // staging-feed decisions in this service (they do NOT change the global
    // CliExecutionContext.IdentityChannel used for hive/packages directory lookups), so a plain
    // local dev build can be made to derive and resolve from a real darc-pub-microsoft-aspire-<sha>
    // feed exactly the way an official staging build would. See docs/cli-staging-validation.md.
    //
    //   overrideCliIdentityChannel        - forces the identity used for staging-feed decisions
    //                                       (validated against the known channel set). Set to
    //                                       `staging` to exercise the staging-identity darc path.
    //   overrideCliInformationalVersion   - forces the AssemblyInformationalVersion that the SHA
    //                                       derivation and version-shape (quality) checks read,
    //                                       e.g. `13.4.0-preview.1.26280.6+<full-commit-hash>`.
    //
    // NOTE: These only route to a feed; they do not create one. They are typically useful only
    // once the darc-pub-microsoft-aspire-<sha> feed actually exists for the specific commit/version
    // you are emulating (i.e. an official build for that SHA has been published). Until then the
    // derived feed URL resolves to nothing and restore will fail to find packages.
    internal const string OverrideCliIdentityChannelConfigKey = "overrideCliIdentityChannel";
    internal const string OverrideCliInformationalVersionConfigKey = "overrideCliInformationalVersion";

    private readonly CliExecutionContext _executionContext;

    /// <summary>
    /// Resolves the nuget.org service-index URL the CLI should write into the
    /// <c>PackageMapping</c>s it generates. Returns the override supplied by
    /// <c>ASPIRE_CLI_NUGET_SERVICE_INDEX</c> or the <c>nugetServiceIndexOverride</c>
    /// field of <c>.aspire-install.json</c> when set, otherwise the canonical
    /// <see cref="PackageSources.NuGetOrg"/>. This is a write-side rewrite
    /// only; URLs read out of existing user <c>NuGet.config</c> files are not
    /// touched. See <c>docs/specs/cli-identity-sidecar.md</c>.
    /// </summary>
    private string NuGetOrgUrl => _executionContext.NuGetServiceIndexOverride ?? PackageSources.NuGetOrg;

    private readonly INuGetPackageCache _nuGetPackageCache;
    private readonly IFeatures _features;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PackagingService> _logger;
    private readonly Func<string?> _processPathProvider;
    // Predicate used by staging-channel synthesis to decide whether the running CLI is built
    // from a stable-shaped version (no semver prerelease tag). Defaults to inspecting the
    // current Aspire.Cli assembly's InformationalVersion; tests inject a deterministic value
    // because the version baked into the test-host assembly varies by build configuration.
    private readonly Func<bool> _isStableShapedCliVersion;
    // Provides the running CLI's AssemblyInformationalVersion (which carries the +<commitHash>
    // build metadata used to derive the SHA-specific darc-pub-microsoft-aspire-<hash> staging
    // feed). Defaults to reading the Aspire.Cli assembly; tests inject a deterministic value
    // because the version baked into the test-host assembly varies by build configuration, which
    // otherwise makes the derived darc feed URL non-deterministic (and therefore un-assertable).
    private readonly Func<string?> _cliInformationalVersionProvider;

    // Cached result of the staging-channel availability check. The inputs (CLI identity,
    // overrideStagingFeed, StagingChannelEnabled feature) are effectively static for the
    // process lifetime, so computing this once avoids re-formatting the localized reason
    // string on every GetChannelsAsync call (callers fan out across NewCommand,
    // UpdateCommand, IntegrationPackageSearchService, NuGetPackagePrefetcher, etc.).
    private readonly Lazy<string?> _stagingUnavailableReasonCache;

    public PackagingService(
        CliExecutionContext executionContext,
        INuGetPackageCache nuGetPackageCache,
        IFeatures features,
        IConfiguration configuration,
        ILogger<PackagingService> logger,
        Func<string?>? processPathProvider = null,
        Func<bool>? isStableShapedCliVersion = null,
        Func<string?>? cliInformationalVersionProvider = null)
    {
        _executionContext = executionContext;
        _nuGetPackageCache = nuGetPackageCache;
        _features = features;
        _configuration = configuration;
        _logger = logger;
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
        _isStableShapedCliVersion = isStableShapedCliVersion ?? IsStableShapedCliVersionDefault;
        _cliInformationalVersionProvider = cliInformationalVersionProvider ?? GetCliInformationalVersionDefault;
        _stagingUnavailableReasonCache = new Lazy<string?>(ComputeStagingChannelUnavailableReason);
    }

    // One-shot guards so the refusal warning / successful-resolution info line are emitted
    // at most once per CLI process instead of on every GetChannelsAsync invocation. Many
    // commands (and the background NuGetPackagePrefetcher) call GetChannelsAsync repeatedly;
    // logging on each call produced excessive noise — particularly the refusal warning when
    // a project's aspire.config.json pins `channel: staging` on a daily/local CLI.
    private int _stagingRefusalLogged;
    private int _stagingResolutionLogged;
    private int _stagingFeedDerivationFailedLogged;
    private int _stagingDiagnosticOverrideLogged;

    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default, string? requestedChannelName = null)
    {
        // Emit the diagnostic-override warning up front so any invocation that has the overrides set
        // leaves a trace, regardless of whether a staging channel ends up being synthesized below
        // (e.g. an override that ultimately resolves to a non-staging identity still warns).
        WarnIfStagingDiagnosticOverridesActive();

        var nugetOrg = NuGetOrgUrl;
        var defaultChannel = PackageChannel.CreateImplicitChannel(_nuGetPackageCache, _features, _logger, currentCliVersion: _executionContext.IdentitySdkVersion);

        var stableChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, new[]
        {
            new PackageMapping(PackageMapping.AllPackages, nugetOrg)
        }, _nuGetPackageCache, _features, _logger, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/ga/daily", currentCliVersion: _executionContext.IdentitySdkVersion);

        var dailyChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Daily, PackageChannelQuality.Prerelease, new[]
        {
            new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json"),
            new PackageMapping(PackageMapping.AllPackages, nugetOrg)
        }, _nuGetPackageCache, _features, _logger, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/daily", currentCliVersion: _executionContext.IdentitySdkVersion);

        var prPackageChannels = new List<PackageChannel>();

        // Cannot use HiveDirectory.Exists here because it blows up on the
        // intermediate directory structure which may not exist in some
        // contexts (e.g. in our Codespace where we have the CLI on the 
        // path but not in the $HOME/.aspire/bin folder).
        if (_executionContext.HivesDirectory.Exists)
        {
            var prHives = _executionContext.HivesDirectory.GetDirectories();
            foreach (var prHive in prHives)
            {
                prPackageChannels.Add(CreateLocalHiveChannel(prHive.Name, new DirectoryInfo(Path.Combine(prHive.FullName, "packages"))));
            }
        }

        if (TryResolvePrInstallPackagesDirectory(TryGetProcessPathForPrInstallDiscovery(), _executionContext.IdentityChannel) is { } prInstallPackagesDirectory)
        {
            // The install-prefix hive belongs to the running PR CLI. Prefer it over a same-named
            // default hive so a stale ~/.aspire/hives/pr-<N> cannot mask the co-installed packages.
            prPackageChannels.RemoveAll(c => string.Equals(c.Name, _executionContext.IdentityChannel, StringComparisons.ChannelName));
            prPackageChannels.Add(CreateLocalHiveChannel(_executionContext.IdentityChannel, prInstallPackagesDirectory));
        }

        var channels = new List<PackageChannel>([defaultChannel, stableChannel]);

        // Add staging channel after stable and before daily. Staging CLI builds should
        // dogfood staging packages even before a project-level channel pin exists, and
        // callers that already resolved a staging channel from another project directory
        // need the channel materialized before they can match it below.
        var stagingChannelConfigured = string.Equals(_configuration["channel"], PackageChannelNames.Staging, StringComparisons.ChannelName);
        var stagingChannelRequested = string.Equals(requestedChannelName, PackageChannelNames.Staging, StringComparisons.ChannelName);
        var stagingIdentityChannel = string.Equals(GetEffectiveIdentityChannel(), PackageChannelNames.Staging, StringComparisons.ChannelName);
        var stagingFeatureEnabled = _features.IsFeatureEnabled(KnownFeatures.StagingChannelEnabled, false);
        if (stagingFeatureEnabled || stagingChannelConfigured || stagingChannelRequested || stagingIdentityChannel)
        {
            // Default quality selection rules (per staging entry point). NOTE: quality controls
            // version FILTERING only (which versions in the feed are eligible); it no longer
            // selects the feed itself. Feed PROVENANCE is identity-driven inside
            // ShouldUseSharedStagingFeed — a staging-identity CLI always resolves Aspire.* from its
            // own SHA-specific darc-pub-microsoft-aspire-<commit> feed.
            //   - Explicit user opt-in (`stagingChannelConfigured`, `stagingChannelRequested`): Both.
            //     The user picked staging deliberately; they get the broadest matching window.
            //   - `stagingFeatureEnabled` only (no other staging signal): Stable. Preserves the
            //     pre-existing behavior of the staging feature flag.
            //   - `stagingIdentityChannel` (the running CLI itself self-identifies as staging):
            //     follows the CLI build's version shape so the eligible version window matches the
            //     packages the build actually shipped.
            //       * Stable-shaped (e.g. "13.4.0", produced during release stabilization when
            //         StabilizePackageVersion=true) → Stable, so resolution prefers the stable-shaped
            //         packages on the darc feed (the #17527 scenario).
            //       * Prerelease-shaped (e.g. "13.4.0-preview.1.123") → Both, so prerelease-tagged
            //         packages on the darc feed remain eligible.
            PackageChannelQuality defaultQuality;
            if (stagingIdentityChannel)
            {
                // When the running CLI's identity itself is staging, the synthesized channel's
                // quality MUST follow the CLI build's version shape regardless of how synthesis
                // was triggered. `init` and many other commands pass requestedChannelName=staging
                // when identity is staging, so checking `stagingChannelRequested` first would
                // short-circuit this path and re-introduce the #17527 version-filtering mismatch on
                // stabilizing builds.
                defaultQuality = _isStableShapedCliVersion()
                    ? PackageChannelQuality.Stable
                    : PackageChannelQuality.Both;
            }
            else if (stagingChannelConfigured || stagingChannelRequested)
            {
                defaultQuality = PackageChannelQuality.Both;
            }
            else
            {
                defaultQuality = PackageChannelQuality.Stable;
            }

            var stagingChannel = CreateStagingChannel(defaultQuality);
            if (stagingChannel is not null)
            {
                channels.Add(stagingChannel);
            }
        }

        // Add daily and PR channels after staging
        channels.Add(dailyChannel);
        channels.AddRange(prPackageChannels);

        if (_executionContext.IdentityPackagesDirectory is { } identityPackagesDirectory)
        {
            // ASPIRE_CLI_PACKAGES / the sidecar `packages` field points the CLI's Aspire* feed directly
            // at a flat directory of freshly built .nupkg files (e.g. artifacts/packages/<Config>/Shipping)
            // instead of a hive staged under ~/.aspire/hives, so a locally built CLI can resolve locally
            // built packages without copying them. The synthesized channel takes the running CLI's
            // identity-channel name and wins over ANY same-named channel synthesized above — whether a
            // built-in (e.g. emulating `daily`/`stable` while supplying local packages), a discovered
            // ~/.aspire/hives/<channel> hive, or a PR-install hive — because the explicit override is the
            // most specific signal of intent. We replace it here (after the full list is built) rather
            // than earlier so built-in channels are covered too. See docs/specs/cli-identity-sidecar.md.
            EnsurePackagesOverrideDirectoryIsUnambiguous(identityPackagesDirectory);
            var replacedChannelCount = channels.RemoveAll(c => string.Equals(c.Name, _executionContext.IdentityChannel, StringComparisons.ChannelName));
            _logger.LogDebug(
                "Synthesized package channel '{Channel}' from the packages override directory '{PackagesDirectory}', replacing {ReplacedChannelCount} same-named channel(s).",
                _executionContext.IdentityChannel,
                identityPackagesDirectory.FullName,
                replacedChannelCount);
            channels.Add(CreateLocalHiveChannel(_executionContext.IdentityChannel, identityPackagesDirectory));
        }

        return Task.FromResult<IEnumerable<PackageChannel>>(channels);
    }

    private string? TryGetProcessPathForPrInstallDiscovery()
    {
        try
        {
            return _processPathProvider();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get process path while discovering PR package hive.");
            return null;
        }
    }

    private PackageChannel CreateLocalHiveChannel(string name, DirectoryInfo packagesDirectory)
    {
        var pinnedVersion = GetLocalHivePinnedVersion(packagesDirectory);

        // Use forward slashes for cross-platform NuGet config compatibility
        var packagesPath = PathNormalizer.NormalizePathForStorage(packagesDirectory.FullName);
        return PackageChannel.CreateExplicitChannel(name, PackageChannelQuality.Both, new[]
        {
            new PackageMapping("Aspire*", packagesPath),
            new PackageMapping(PackageMapping.AllPackages, NuGetOrgUrl)
        }, _nuGetPackageCache, _features, _logger, pinnedVersion: pinnedVersion, currentCliVersion: _executionContext.IdentitySdkVersion);
    }

    internal static DirectoryInfo? TryResolvePrInstallPackagesDirectory(string? processPath, string identityChannel)
    {
        if (!identityChannel.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        DirectoryInfo binaryDirectory;
        try
        {
            var binaryDirectoryPath = Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (string.IsNullOrEmpty(binaryDirectoryPath))
            {
                return null;
            }

            binaryDirectory = new DirectoryInfo(binaryDirectoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }

        // Archive installs from get-aspire-cli-pr place the binary at:
        //   <prefix>/dogfood/pr-<N>/bin/aspire
        // and the matching packages at:
        //   <prefix>/hives/pr-<N>/packages
        if (!string.Equals(binaryDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase) ||
            binaryDirectory.Parent is not { } prDirectory ||
            !string.Equals(prDirectory.Name, identityChannel, StringComparisons.ChannelName) ||
            prDirectory.Parent is not { } dogfoodDirectory ||
            !string.Equals(dogfoodDirectory.Name, "dogfood", StringComparison.OrdinalIgnoreCase) ||
            dogfoodDirectory.Parent is not { } installPrefix)
        {
            return null;
        }

        var packagesDirectory = new DirectoryInfo(Path.Combine(installPrefix.FullName, "hives", identityChannel, "packages"));
        return packagesDirectory.Exists ? packagesDirectory : null;
    }

    // Returns true when the running CLI's identity version is stable-shaped (no semver prerelease
    // tag). Used by the staging-channel synthesis to route stabilizing builds to the SHA-derived
    // darc feed instead of the shared dotnet9 daily feed. Reads CliExecutionContext.IdentitySdkVersion
    // so ASPIRE_CLI_VERSION / sidecar overrides drive the routing, not the physical binary version.
    private bool IsStableShapedCliVersionFromIdentity()
    {
        var version = _executionContext.IdentitySdkVersion;
        return !string.IsNullOrEmpty(version) && !version.Contains('-');
    }

    // Default version-shape predicate. Honors the overrideCliInformationalVersion diagnostic
    // override (so a locally built CLI can present as stable- or prerelease-shaped for staging
    // validation) before falling back to the resolved identity version (which already reflects
    // ASPIRE_CLI_VERSION / the install sidecar).
    private bool IsStableShapedCliVersionDefault()
    {
        var overrideVersion = _configuration[OverrideCliInformationalVersionConfigKey];
        if (!string.IsNullOrEmpty(overrideVersion))
        {
            // Stable-shaped == no semver prerelease tag. Strip build metadata (+<hash>) first so a
            // commit hash that happens to contain '-' can't be misread as a prerelease tag. Example:
            //   "13.4.0-preview.1.26280.6+abcd-ef12" -> version part "13.4.0-preview.1.26280.6" -> prerelease
            //   "13.4.0+abcd-ef12"                   -> version part "13.4.0"                   -> stable
            return !StripBuildMetadata(overrideVersion).Contains('-');
        }

        return IsStableShapedCliVersionFromIdentity();
    }

    // Default informational-version provider. Honors the overrideCliInformationalVersion diagnostic
    // override (so the SHA-specific darc feed can be derived deterministically from a locally built
    // CLI) before falling back to the resolved identity. IdentityVersion carries the version without
    // build metadata and IdentityCommit carries the +<sha>; they are recombined here so the
    // staging-feed SHA derivation honors ASPIRE_CLI_VERSION / ASPIRE_CLI_COMMIT / the install sidecar
    // rather than reading the physical assembly informational version.
    private string? GetCliInformationalVersionDefault()
    {
        var overrideVersion = _configuration[OverrideCliInformationalVersionConfigKey];
        if (!string.IsNullOrEmpty(overrideVersion))
        {
            return overrideVersion;
        }

        var version = _executionContext.IdentityVersion;
        var commit = _executionContext.IdentityCommit;
        return string.IsNullOrEmpty(commit) ? version : $"{version}+{commit}";
    }

    private static string StripBuildMetadata(string version)
    {
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }

    // Returns the identity channel used for staging-feed routing decisions. Normally this is the
    // CLI build's baked identity (CliExecutionContext.IdentityChannel). For local validation of
    // staging feed routing, overrideCliIdentityChannel can force a different identity (validated
    // against the known channel set via IdentityChannelReader.IsValidChannel) WITHOUT changing the
    // global identity used elsewhere (hive/packages directory lookups), keeping the blast radius
    // limited to feed provenance. Invalid override values are ignored — we fall back to the real
    // identity, mirroring how overrideStagingFeed ignores malformed URLs.
    private string GetEffectiveIdentityChannel()
    {
        var overrideChannel = _configuration[OverrideCliIdentityChannelConfigKey];
        if (!string.IsNullOrEmpty(overrideChannel) && IdentityChannelReader.IsValidChannel(overrideChannel))
        {
            return overrideChannel;
        }

        return _executionContext.IdentityChannel;
    }

    // Emits a single warning when either staging diagnostic override is active, so a normal CLI
    // invocation can't silently resolve Aspire.* from an overridden identity/feed without a trace
    // in the logs. Emitted at most once per process to avoid noise across repeated GetChannelsAsync
    // calls.
    private void WarnIfStagingDiagnosticOverridesActive()
    {
        var identityOverride = _configuration[OverrideCliIdentityChannelConfigKey];
        var versionOverride = _configuration[OverrideCliInformationalVersionConfigKey];
        if (string.IsNullOrEmpty(identityOverride) && string.IsNullOrEmpty(versionOverride))
        {
            return;
        }

        if (Interlocked.Exchange(ref _stagingDiagnosticOverrideLogged, 1) == 0)
        {
            _logger.LogWarning(
                "Staging feed-routing diagnostic overrides are active: {IdentityKey}={IdentityValue}, {VersionKey}={VersionValue}. " +
                "These are intended only for local validation of staging feed routing and must not be set on a normal CLI.",
                OverrideCliIdentityChannelConfigKey,
                string.IsNullOrEmpty(identityOverride) ? "(unset)" : identityOverride,
                OverrideCliInformationalVersionConfigKey,
                string.IsNullOrEmpty(versionOverride) ? "(unset)" : versionOverride);
        }
    }

    private PackageChannel? CreateStagingChannel(PackageChannelQuality defaultQuality)
    {
        // Refuse to synthesize a staging channel on CLI identities that cannot produce a real
        // staging feed (daily, local, pr-<N>). Silently falling back to the shared daily feed or
        // a non-existent SHA-specific darc feed is the bug tracked by
        // https://github.com/microsoft/aspire/issues/16652. The escape hatches (explicit
        // overrideStagingFeed, or the StagingChannelEnabled feature flag) are honored inside
        // IsStagingChannelSynthesisAllowed below.
        var unavailableReason = GetStagingChannelUnavailableReason();
        if (unavailableReason is not null)
        {
            if (Interlocked.Exchange(ref _stagingRefusalLogged, 1) == 0)
            {
                _logger.LogWarning("Refusing to synthesize 'staging' package channel: {Reason}", unavailableReason);
            }
            return null;
        }

        var stagingQuality = GetStagingQuality(defaultQuality);
        var hasExplicitFeedOverride = !string.IsNullOrEmpty(_configuration[OverrideStagingFeedConfigKey]);

        // Feed PROVENANCE is decided by the CLI build identity; version FILTERING is decided by
        // quality. These are independent concerns and must not be conflated (see
        // https://github.com/microsoft/aspire/issues/16652 for the original misroute, and the
        // staging-identity prerelease regression that motivated separating them).
        var effectiveIdentityChannel = GetEffectiveIdentityChannel();
        var useSharedFeed = ShouldUseSharedStagingFeed(hasExplicitFeedOverride, stagingQuality, effectiveIdentityChannel);

        var stagingFeedUrl = GetStagingFeedUrl(useSharedFeed);
        if (stagingFeedUrl is null)
        {
            // Reaching here means synthesis was allowed (IsStagingChannelSynthesisAllowed passed) but the
            // feed URL could not be produced. The only way that happens without an explicit override is the
            // darc path failing to derive a commit hash from the CLI's AssemblyInformationalVersion (null,
            // or no '+<hash>' build metadata). For a staging-identity CLI this should not occur on an
            // officially published build, so surface it as a warning rather than silently dropping the
            // channel — otherwise the caller just sees a missing 'staging' channel with no diagnostic
            // (GetStagingChannelUnavailableReason() returns null because synthesis was permitted).
            if (Interlocked.Exchange(ref _stagingFeedDerivationFailedLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Could not synthesize 'staging' package channel: failed to derive a staging feed URL for CLI identity '{Identity}' (no commit hash in the CLI version and no overrideStagingFeed set).",
                    effectiveIdentityChannel);
            }
            return null;
        }

        var pinnedVersion = GetStagingPinnedVersion(useSharedFeed);

        var stagingChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Staging, stagingQuality, new[]
        {
            new PackageMapping("Aspire*", stagingFeedUrl),
            new PackageMapping(PackageMapping.AllPackages, NuGetOrgUrl)
        }, _nuGetPackageCache, _features, _logger, configureGlobalPackagesFolder: !useSharedFeed, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/rc/daily", pinnedVersion: pinnedVersion, currentCliVersion: _executionContext.IdentitySdkVersion);

        // Surface the resolved staging routing so users can see what `--channel staging` actually
        // picked (the "show what was resolved" suggestion from the issue RCA). Pinned version is
        // optional and only set when configured via stagingPinToCliVersion. Emitted once per
        // process to avoid repeating on every GetChannelsAsync call.
        if (Interlocked.Exchange(ref _stagingResolutionLogged, 1) == 0)
        {
            _logger.LogInformation(
                "Resolved 'staging' channel: feed={FeedUrl}, quality={Quality}, pinnedVersion={PinnedVersion}",
                stagingFeedUrl,
                stagingQuality,
                pinnedVersion ?? "(none)");
        }

        return stagingChannel;
    }

    /// <inheritdoc />
    public string? GetStagingChannelUnavailableReason() => _stagingUnavailableReasonCache.Value;

    // Decides whether the synthesized staging channel routes Aspire.* at the SHARED dnceng/dotnet9
    // daily feed (true) or at the SHA-specific darc-pub-microsoft-aspire-<commit> feed (false).
    //
    // The rule is identity-driven, NOT version-shape-driven:
    //   * Explicit overrideStagingFeed  -> false. The caller named an exact feed; GetStagingFeedUrl
    //     returns it verbatim, so the shared-vs-darc distinction is moot.
    //   * staging IDENTITY              -> false (always its own darc feed, any version shape). A CLI
    //     whose baked AspireCliChannel is `staging` is an officially published release-branch build,
    //     and darc publishes a per-commit darc-pub-microsoft-aspire-<commit> feed for EVERY such
    //     build — prerelease-shaped 13.4.0-preview.* and stable-shaped 13.4.0 alike. That feed is
    //     derived from the CLI's own commit, so it always carries the CLI's matching packages.
    //     Falling back to the shared dotnet9 daily feed (which only carries main-branch daily
    //     packages) silently resolves the wrong packages for polyglot apphosts while C# apphosts —
    //     whose nuget.config has the darc feed baked in — resolve correctly. That asymmetry is the
    //     bug this method fixes. (A missing darc feed for an officially published staging build is a
    //     publish/infra failure that should surface as an unresolved package, not be masked by a
    //     silent downgrade to daily packages.)
    //   * any other identity opting into staging (stable identity via config pin / StagingChannelEnabled
    //     feature) -> keep the historical quality-based routing: non-Stable quality uses the shared
    //     feed, Stable quality uses the SHA feed. Those identities do not own a release-branch darc
    //     feed of their own, so this preserves prior behavior unchanged.
    private static bool ShouldUseSharedStagingFeed(bool hasExplicitFeedOverride, PackageChannelQuality stagingQuality, string identityChannel)
    {
        if (hasExplicitFeedOverride)
        {
            return false;
        }

        if (string.Equals(identityChannel, PackageChannelNames.Staging, StringComparisons.ChannelName))
        {
            return false;
        }

        return stagingQuality is not PackageChannelQuality.Stable;
    }

    private string? ComputeStagingChannelUnavailableReason()
    {
        if (IsStagingChannelSynthesisAllowed())
        {
            return null;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            PackagingStrings.StagingChannelUnavailableOnDailyCli,
            GetEffectiveIdentityChannel());
    }

    private bool IsStagingChannelSynthesisAllowed()
    {
        // Explicit feed override always wins: the caller has told us exactly which feed to use,
        // so we don't need to infer one from the CLI identity.
        if (!string.IsNullOrEmpty(_configuration[OverrideStagingFeedConfigKey]))
        {
            return true;
        }

        // The staging feature flag is an explicit developer/test opt-in that predates this
        // gating; preserve it for back-compat with existing developer workflows.
        if (_features.IsFeatureEnabled(KnownFeatures.StagingChannelEnabled, false))
        {
            return true;
        }

        // Only stable and staging CLI builds can deterministically resolve a staging feed:
        //   - stable: the SHA-specific darc-pub-microsoft-aspire-<hash> feed exists for the
        //     stable release branch commit baked into the CLI.
        //   - staging: dogfoods staging packages (see #17155 which auto-registers the staging
        //     channel for the staging CLI identity).
        // For daily, local, and pr-<N> identities, falling back to either the SHA feed (no real
        // darc feed exists) or the shared daily feed silently resolves daily packages — the
        // exact bug tracked by https://github.com/microsoft/aspire/issues/16652.
        return string.Equals(GetEffectiveIdentityChannel(), PackageChannelNames.Stable, StringComparisons.ChannelName)
            || string.Equals(GetEffectiveIdentityChannel(), PackageChannelNames.Staging, StringComparisons.ChannelName);
    }

    private string? GetStagingFeedUrl(bool useSharedFeed)
    {
        // Check for _configuration override first
        var overrideFeed = _configuration[OverrideStagingFeedConfigKey];
        if (!string.IsNullOrEmpty(overrideFeed))
        {
            // Validate that the override URL is well-formed
            if (UrlHelper.IsHttpUrl(overrideFeed))
            {
                return overrideFeed;
            }
            // Invalid URL, fall through to default behavior
        }

        // Use the shared daily feed when the routing policy selected it (see ShouldUseSharedStagingFeed).
        if (useSharedFeed)
        {
            return "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";
        }

        // Derive the SHA-specific staging feed from the CLI's own commit. The commit comes from
        // CliExecutionContext.IdentityCommit (ASPIRE_CLI_COMMIT → install sidecar → assembly build
        // metadata), so a locally built CLI emulating an official build derives the same feed. The
        // injectable provider is still consulted first so tests can pin a deterministic
        // informational version (the test-host assembly version is non-deterministic). Example:
        //   informational "13.4.0-preview.1.26280.6+abcdef12...." or IdentityCommit "abcdef12...."
        // yields the feed:
        //   https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json
        var commitHash = ExtractCommitFromInformationalVersion(_cliInformationalVersionProvider())
            ?? (string.IsNullOrEmpty(_executionContext.IdentityCommit) ? null : _executionContext.IdentityCommit);

        if (string.IsNullOrEmpty(commitHash))
        {
            return null;
        }

        // Env/sidecar commits are guaranteed >= 8 chars by IdentityResolver.ValidateCommit; only the
        // never-validated assembly informational version could be shorter (e.g. a test or malformed
        // build), so fall back to the raw value there rather than throwing on this resolve path.
        var truncatedHash = commitHash.Length >= 8 ? commitHash[..8] : commitHash;

        return $"https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-{truncatedHash}/nuget/v3/index.json";
    }

    // Extracts the commit hash from the build-metadata suffix of an informational version, e.g.
    //   "13.4.0-preview.1.26280.6+abcdef1234..." -> "abcdef1234..."
    // Returns null when there is no usable "+<sha>" suffix.
    private static string? ExtractCommitFromInformationalVersion(string? informationalVersion)
    {
        if (informationalVersion is null)
        {
            return null;
        }

        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0 || plusIndex + 1 >= informationalVersion.Length)
        {
            return null;
        }

        return informationalVersion[(plusIndex + 1)..];
    }

    private PackageChannelQuality GetStagingQuality(PackageChannelQuality defaultQuality)
    {
        // Check for _configuration override
        var overrideQuality = _configuration["overrideStagingQuality"];
        if (!string.IsNullOrEmpty(overrideQuality))
        {
            // Try to parse the quality value (case-insensitive)
            if (Enum.TryParse<PackageChannelQuality>(overrideQuality, ignoreCase: true, out var quality))
            {
                return quality;
            }
        }

        // Preserve the historical safe fallback for invalid override values while allowing
        // different staging entry points to choose a better default when no override is set.
        return string.IsNullOrEmpty(overrideQuality) ? defaultQuality : PackageChannelQuality.Stable;
    }

    private string? GetStagingPinnedVersion(bool useSharedFeed)
    {
        // Only pin versions when using the shared feed and the config flag is set
        var pinToCliVersion = _configuration["stagingPinToCliVersion"];
        if (!useSharedFeed || !string.Equals(pinToCliVersion, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Pin to the running CLI's identity version (honors ASPIRE_CLI_VERSION / the install
        // sidecar / the overrideCliInformationalVersion diagnostic), stripped of build metadata.
        var informationalVersion = _cliInformationalVersionProvider();
        return string.IsNullOrEmpty(informationalVersion) ? null : StripBuildMetadata(informationalVersion);
    }

    // Local hive channels point at a flat directory of .nupkg files instead of a searchable feed.
    // Derive a concrete Aspire version from the hive contents and pin the channel to it so template
    // and package resolution stays on the same locally built version instead of asking NuGet for "latest".
    // Prefer Aspire.ProjectTemplates because it drives `aspire new`, then fall back to common packages
    // that are still present when the templates package is absent.
    private static string? GetLocalHivePinnedVersion(DirectoryInfo packagesDirectory)
    {
        if (!packagesDirectory.Exists)
        {
            return null;
        }

        return FindHighestVersion("Aspire.ProjectTemplates")
            ?? FindHighestVersion("Aspire.Hosting")
            ?? FindHighestVersion("Aspire.AppHost.Sdk");

        string? FindHighestVersion(string packageId)
        {
            return packagesDirectory
                .EnumerateFiles($"{packageId}.*.nupkg")
                .Select(static file => file.Name)
                .Select(fileName => fileName[(packageId.Length + 1)..^".nupkg".Length])
                .Where(version => SemVersion.TryParse(version, SemVersionStyles.Strict, out _))
                .OrderByDescending(version => SemVersion.Parse(version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
                .FirstOrDefault();
        }
    }

    // The ASPIRE_CLI_PACKAGES override maps Aspire* -> a flat directory feed. A flat directory has no
    // "latest stable vs latest prerelease" semantics: when an id appears with more than one version,
    // NuGet resolves the highest, silently masking the version the developer meant to test. Fail fast
    // with an actionable message instead of resolving the wrong package. Only Aspire* ids are checked
    // because the synthesized channel only routes Aspire* to this directory (everything else goes to
    // nuget.org), so unrelated multi-versioned packages in the same folder are harmless.
    private static void EnsurePackagesOverrideDirectoryIsUnambiguous(DirectoryInfo packagesDirectory)
    {
        if (!packagesDirectory.Exists)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                PackagingStrings.PackagesOverrideDirectoryNotFound,
                packagesDirectory.FullName));
        }

        var versionsById = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in packagesDirectory.EnumerateFiles("Aspire*.nupkg"))
        {
            if (!TryParseNupkgFileName(file.Name, out var id, out var version))
            {
                continue;
            }

            if (!versionsById.TryGetValue(id, out var versions))
            {
                versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                versionsById[id] = versions;
            }

            versions.Add(version);
        }

        var duplicates = versionsById
            .Where(static kvp => kvp.Value.Count > 1)
            .OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicates.Count > 0)
        {
            // e.g. "Aspire.Hosting (13.4.1, 13.4.2); Aspire.Hosting.Azure (13.4.1, 13.4.2)"
            var details = string.Join("; ", duplicates.Select(static kvp =>
                $"{kvp.Key} ({string.Join(", ", kvp.Value)})"));
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                PackagingStrings.PackagesOverrideDirectoryHasDuplicateVersions,
                packagesDirectory.FullName,
                details));
        }
    }

    // Splits a NuGet package file name into its package id and version. The file name shape is
    // "<id>.<version>.nupkg", where <id> may itself contain dots (e.g. "Aspire.Hosting.Azure.Storage")
    // and <version> is a strict SemVer that always begins with a numeric major component. Example:
    //   "Aspire.Hosting.Azure.Storage.13.4.0-preview.1.25366.3.nupkg"
    //     -> id = "Aspire.Hosting.Azure.Storage", version = "13.4.0-preview.1.25366.3"
    // The first '.'-delimited segment that begins with a digit marks the start of the version, because
    // package-id segments never start with a digit. Returns false (and the file is ignored by the
    // guardrail) when no split yields a strict-SemVer version.
    internal static bool TryParseNupkgFileName(string fileName, out string id, out string version)
    {
        id = string.Empty;
        version = string.Empty;

        const string suffix = ".nupkg";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^suffix.Length];
        var parts = stem.Split('.');
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !char.IsDigit(parts[i][0]))
            {
                continue;
            }

            var candidateVersion = string.Join('.', parts[i..]);
            if (SemVersion.TryParse(candidateVersion, SemVersionStyles.Strict, out _))
            {
                id = string.Join('.', parts[..i]);
                version = candidateVersion;
                return true;
            }
        }

        return false;
    }
}
