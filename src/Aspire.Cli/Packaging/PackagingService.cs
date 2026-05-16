// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;
using System.Reflection;

namespace Aspire.Cli.Packaging;

internal interface IPackagingService
{
    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default);
}

internal class PackagingService(CliExecutionContext executionContext, INuGetPackageCache nuGetPackageCache, IFeatures features, IConfiguration configuration, ILogger<PackagingService> logger) : IPackagingService
{
    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var defaultChannel = PackageChannel.CreateImplicitChannel(nuGetPackageCache, logger);
        
        var stableChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        }, nuGetPackageCache, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/ga/daily", logger: logger);

        var dailyChannel = PackageChannel.CreateExplicitChannel(PackageChannelNames.Daily, PackageChannelQuality.Prerelease, new[]
        {
            new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json"),
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        }, nuGetPackageCache, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/daily", logger: logger);

        var prPackageChannels = new List<PackageChannel>();

        // Cannot use HiveDirectory.Exists here because it blows up on the
        // intermediate directory structure which may not exist in some
        // contexts (e.g. in our Codespace where we have the CLI on the 
        // path but not in the $HOME/.aspire/bin folder).
        if (executionContext.HivesDirectory.Exists)
        {
            var prHives = executionContext.HivesDirectory.GetDirectories();
            foreach (var prHive in prHives)
            {
                // The packages subdirectory contains the actual .nupkg files
                var packagesDirectory = new DirectoryInfo(Path.Combine(prHive.FullName, "packages"));
                var pinnedVersion = GetLocalHivePinnedVersion(packagesDirectory);

                // Use forward slashes for cross-platform NuGet config compatibility
                var packagesPath = packagesDirectory.FullName.Replace('\\', '/');
                var prChannel = PackageChannel.CreateExplicitChannel(prHive.Name, PackageChannelQuality.Both, new[]
                {
                    new PackageMapping("Aspire*", packagesPath),
                    new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
                }, nuGetPackageCache, pinnedVersion: pinnedVersion, logger: logger);

                prPackageChannels.Add(prChannel);
            }
        }

        var channels = new List<PackageChannel>([defaultChannel, stableChannel]);

        // Add staging channel if feature is enabled (after stable, before daily)
        if (KnownFeatures.IsStagingChannelEnabled(features, configuration))
        {
            var stagingChannel = CreateStagingChannel();
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

    private PackageChannel? CreateStagingChannel()
    {
        var stagingQuality = GetStagingQuality();
        var hasExplicitFeedOverride = !string.IsNullOrEmpty(configuration["overrideStagingFeed"]);

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
            new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
        }, nuGetPackageCache, configureGlobalPackagesFolder: !useSharedFeed, cliDownloadBaseUrl: "https://aka.ms/dotnet/9/aspire/rc/daily", pinnedVersion: pinnedVersion, logger: logger);

        return stagingChannel;
    }

    private string? GetStagingFeedUrl(bool useSharedFeed)
    {
        // Check for configuration override first
        var overrideFeed = configuration["overrideStagingFeed"];
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

    private PackageChannelQuality GetStagingQuality()
    {
        // Check for configuration override
        var overrideQuality = configuration["overrideStagingQuality"];
        if (!string.IsNullOrEmpty(overrideQuality))
        {
            // Try to parse the quality value (case-insensitive)
            if (Enum.TryParse<PackageChannelQuality>(overrideQuality, ignoreCase: true, out var quality))
            {
                return quality;
            }
        }

        // Default to Stable if not specified or invalid
        return PackageChannelQuality.Stable;
    }

    private string? GetStagingPinnedVersion(bool useSharedFeed)
    {
        // Only pin versions when using the shared feed and the config flag is set
        var pinToCliVersion = configuration["stagingPinToCliVersion"];
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
