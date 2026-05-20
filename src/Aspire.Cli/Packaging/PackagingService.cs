// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;
using System.Globalization;
using System.Reflection;
using System.Security;

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
    /// the SHA-specific darc feed (<c>darc-pub-microsoft-aspire-&lt;hash&gt;</c>) only exists
    /// for stable release branch builds, and falling back to the shared daily feed silently
    /// resolves daily packages instead of staging ones. To avoid that downgrade
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

    private readonly CliExecutionContext _executionContext;
    private readonly INuGetPackageCache _nuGetPackageCache;
    private readonly IFeatures _features;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PackagingService> _logger;
    private readonly Func<string?> _processPathProvider;

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
        Func<string?>? processPathProvider = null)
    {
        _executionContext = executionContext;
        _nuGetPackageCache = nuGetPackageCache;
        _features = features;
        _configuration = configuration;
        _logger = logger;
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
        _stagingUnavailableReasonCache = new Lazy<string?>(ComputeStagingChannelUnavailableReason);
    }

    // One-shot guards so the refusal warning / successful-resolution info line are emitted
    // at most once per CLI process instead of on every GetChannelsAsync invocation. Many
    // commands (and the background NuGetPackagePrefetcher) call GetChannelsAsync repeatedly;
    // logging on each call produced excessive noise — particularly the refusal warning when
    // a project's aspire.config.json pins `channel: staging` on a daily/local CLI.
    private int _stagingRefusalLogged;
    private int _stagingResolutionLogged;

    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default, string? requestedChannelName = null)
    {
        var defaultChannel = PackageChannel.CreateImplicitChannel(_nuGetPackageCache, _features, _logger);
        
        var stableChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, new[]
        {
            new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
        }, _nuGetPackageCache, _features, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/ga/daily", logger: _logger);

        var dailyChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Daily, PackageChannelQuality.Prerelease, new[]
        {
            new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json"),
            new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
        }, _nuGetPackageCache, _features, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/daily", logger: _logger);

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
        var stagingIdentityChannel = string.Equals(_executionContext.IdentityChannel, PackageChannelNames.Staging, StringComparisons.ChannelName);
        var stagingFeatureEnabled = _features.IsFeatureEnabled(KnownFeatures.StagingChannelEnabled, false);
        if (stagingFeatureEnabled || stagingChannelConfigured || stagingChannelRequested || stagingIdentityChannel)
        {
            var defaultQuality = stagingChannelConfigured || stagingChannelRequested || stagingIdentityChannel ? PackageChannelQuality.Both : PackageChannelQuality.Stable;
            var stagingChannel = CreateStagingChannel(defaultQuality);
            if (stagingChannel is not null)
            {
                channels.Add(stagingChannel);
            }
        }

        // Add daily and PR channels after staging
        channels.Add(dailyChannel);
        channels.AddRange(prPackageChannels);

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
            new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
        }, _nuGetPackageCache, _features, pinnedVersion: pinnedVersion, logger: _logger);
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

        // When quality is Prerelease or Both and no explicit feed override is set,
        // use the shared daily feed instead of the SHA-specific feed. SHA-specific
        // darc-pub-* feeds are only created for stable-quality builds, so a non-Stable
        // quality without an explicit feed override can only work with the shared feed.
        var useSharedFeed = !hasExplicitFeedOverride &&
                            stagingQuality is not PackageChannelQuality.Stable;

        var stagingFeedUrl = GetStagingFeedUrl(useSharedFeed);
        if (stagingFeedUrl is null)
        {
            return null;
        }

        var pinnedVersion = GetStagingPinnedVersion(useSharedFeed);

        var stagingChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Staging, stagingQuality, new[]
        {
            new PackageMapping("Aspire*", stagingFeedUrl),
            new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
        }, _nuGetPackageCache, _features, configureGlobalPackagesFolder: !useSharedFeed, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/rc/daily", pinnedVersion: pinnedVersion, logger: _logger);

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

    private string? ComputeStagingChannelUnavailableReason()
    {
        if (IsStagingChannelSynthesisAllowed())
        {
            return null;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            PackagingStrings.StagingChannelUnavailableOnDailyCli,
            _executionContext.IdentityChannel);
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
        return string.Equals(_executionContext.IdentityChannel, PackageChannelNames.Stable, StringComparisons.ChannelName)
            || string.Equals(_executionContext.IdentityChannel, PackageChannelNames.Staging, StringComparisons.ChannelName);
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

        // Use the shared daily feed when builds aren't marked stable
        if (useSharedFeed)
        {
            return "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";
        }

        // Extract commit hash from assembly version to build staging feed URL
        // Staging feed URL template: https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-{commitHash}/nuget/v3/index.json
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (informationalVersion is null)
        {
            return null;
        }

        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0 || plusIndex + 1 >= informationalVersion.Length)
        {
            return null;
        }

        var commitHash = informationalVersion[(plusIndex + 1)..];
        var truncatedHash = commitHash.Length >= 8 ? commitHash[..8] : commitHash;
        
        return $"https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-{truncatedHash}/nuget/v3/index.json";
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

        // Get the CLI's own version and strip build metadata (+hash)
        var cliVersion = Utils.VersionHelper.GetDefaultTemplateVersion();
        var plusIndex = cliVersion.IndexOf('+');
        return plusIndex >= 0 ? cliVersion[..plusIndex] : cliVersion;
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
}
