// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Commands;

internal sealed class IntegrationPackageSearchService(
    IPackagingService packagingService,
    INuGetPackageCache nuGetPackageCache,
    IProjectLocator projectLocator,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IAppHostProjectFactory projectFactory,
    IPackageTagMetadataService packageTagMetadataService)
{
    private const double FuzzyMatchThreshold = 0.3;
    private const int MaxThirdPartyVerificationConcurrency = 16;
    private const string RequestedSourceChannelName = "source";
    private const string ThirdPartyChannelName = "third-party";

    public async Task<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>> GetIntegrationPackagesWithChannelsAsync(DirectoryInfo workingDirectory, string? configuredChannel, IntegrationDiscoveryScope discoveryScope, string? requestedSource = null, CancellationToken cancellationToken = default)
    {
        var channels = await GetApplicableChannelsAsync(workingDirectory, configuredChannel, discoveryScope, cancellationToken, requestedSource);
        var thirdPartyPackageAllowlist = IntegrationDiscoveryScopeHelpers.GetConfiguredThirdPartyPackages(workingDirectory);
        var packages = new List<(NuGetPackage Package, PackageChannel Channel)>();
        var packagesLock = new object();

        await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
        {
            var integrationPackages = (await channel.SearchPackagesAsync(
                HostingIntegrationMetadata.DiscoveryQuery,
                workingDirectory,
                packageId => discoveryScope.IsPackageAllowed(packageId, thirdPartyPackageAllowlist) &&
                    !HostingIntegrationMetadata.IsKnownNonHostingAspirePackageId(packageId) &&
                    !DeprecatedPackages.IsDeprecated(packageId),
                ct)).ToArray();

            if (discoveryScope is not IntegrationDiscoveryScope.ThirdParty && VersionHelper.IsLocalBuildChannel(channel.Name))
            {
                var builtInPackages = await channel.SearchPackagesAsync(
                    "Aspire.Hosting",
                    workingDirectory,
                    static packageId => HostingIntegrationMetadata.IsBuiltInHostingPackageId(packageId) &&
                        !DeprecatedPackages.IsDeprecated(packageId),
                    ct);

                integrationPackages = [.. integrationPackages, .. builtInPackages];
            }

            lock (packagesLock)
            {
                packages.AddRange(integrationPackages.Select(p => (p, channel)));
            }
        });

        return packages.DistinctBy(static package => $"{package.Channel.Name}\0{package.Package.Id}\0{package.Package.Version}", StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>> GetPackagesByExactIdWithChannelsAsync(DirectoryInfo workingDirectory, string packageId, string? configuredChannel, IntegrationDiscoveryScope discoveryScope, CancellationToken cancellationToken, string? requestedSource = null)
    {
        var thirdPartyPackageAllowlist = IntegrationDiscoveryScopeHelpers.GetConfiguredThirdPartyPackages(workingDirectory);
        if (!discoveryScope.IsPackageAllowed(packageId, thirdPartyPackageAllowlist))
        {
            return [];
        }

        var channels = await GetApplicableChannelsAsync(workingDirectory, configuredChannel, discoveryScope, cancellationToken, requestedSource);
        var packages = new List<(NuGetPackage Package, PackageChannel Channel)>();
        var packagesLock = new object();

        await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
        {
            var channelPackages = (await channel.GetPackagesAsync(packageId, workingDirectory, ct)).ToArray();
            channelPackages = [.. channelPackages.Where(static package => !DeprecatedPackages.IsDeprecated(package.Id))];
            if (channelPackages.Length == 0)
            {
                return;
            }

            var verifiedPackages = HostingIntegrationMetadata.IsBuiltInHostingPackageId(packageId)
                ? channelPackages
                : (await FilterThirdPartyIntegrationPackagesAsync(channel, workingDirectory, channelPackages, ct)).ToArray();

            if (verifiedPackages.Length == 0)
            {
                return;
            }

            lock (packagesLock)
            {
                packages.AddRange(verifiedPackages.Select(p => (p, channel)));
            }
        });

        return packages.DistinctBy(static package => $"{package.Channel.Name}\0{package.Package.Id}\0{package.Package.Version}", StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>> SearchBuiltInPackagesByExactIdWithChannelsAsync(DirectoryInfo workingDirectory, string packageId, string? configuredChannel, CancellationToken cancellationToken, string? requestedSource = null)
    {
        if (!HostingIntegrationMetadata.IsBuiltInHostingPackageId(packageId))
        {
            return [];
        }

        var channels = await GetApplicableChannelsAsync(workingDirectory, configuredChannel, IntegrationDiscoveryScope.Official, cancellationToken, requestedSource);
        var packages = new List<(NuGetPackage Package, PackageChannel Channel)>();
        var packagesLock = new object();

        await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
        {
            var channelPackages = (await channel.SearchPackagesAsync(
                packageId,
                workingDirectory,
                id => string.Equals(id, packageId, StringComparisons.NuGetPackageId) &&
                    !DeprecatedPackages.IsDeprecated(id),
                ct)).ToArray();

            if (channelPackages.Length == 0)
            {
                return;
            }

            lock (packagesLock)
            {
                packages.AddRange(channelPackages.Select(p => (p, channel)));
            }
        });

        return packages.DistinctBy(static package => $"{package.Channel.Name}\0{package.Package.Id}\0{package.Package.Version}", StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(DirectoryInfo WorkingDirectory, string? ConfiguredChannel, int? ExitCode)> GetPackageSearchContextAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        FileInfo? appHostProjectFile;
        if (passedAppHostProjectFile is not null)
        {
            var searchResult = await projectLocator.UseOrFindAppHostProjectFileAsync(
                passedAppHostProjectFile,
                MultipleAppHostProjectsFoundBehavior.Throw,
                createSettingsFile: false,
                cancellationToken);

            appHostProjectFile = searchResult.SelectedProjectFile;
        }
        else
        {
            appHostProjectFile = await projectLocator.GetAppHostFromSettingsAsync(cancellationToken);
        }

        if (appHostProjectFile is null)
        {
            return (executionContext.WorkingDirectory, ConfiguredChannel: null, ExitCode: null);
        }

        var project = projectFactory.GetProject(appHostProjectFile);
        var (configuredChannel, exitCode) = GetConfiguredChannel(appHostProjectFile, project);
        return (appHostProjectFile.Directory!, configuredChannel, exitCode);
    }

    public (string? ConfiguredChannel, int? ExitCode) GetConfiguredChannel(FileInfo appHostProjectFile, IAppHostProject project)
    {
        // For non-.NET projects, read the channel from the local Aspire configuration if available.
        // Unlike .NET projects which have a nuget.config, polyglot apphosts persist the channel
        // in aspire.config.json (or the legacy settings.json during migration).
        if (project.LanguageId == KnownLanguageId.CSharp)
        {
            return (ConfiguredChannel: null, ExitCode: null);
        }

        var appHostDirectory = appHostProjectFile.Directory!.FullName;
        var isProjectReferenceMode = project.IsUsingProjectReferences(appHostProjectFile);
        if (isProjectReferenceMode)
        {
            return (ConfiguredChannel: null, ExitCode: null);
        }

        // TODO: Remove legacy AspireJsonConfiguration fallback once confident most users
        // have migrated. Tracked by https://github.com/microsoft/aspire/issues/15239
        try
        {
            return (AspireConfigFile.Load(appHostDirectory)?.Channel
                ?? AspireJsonConfiguration.Load(appHostDirectory)?.Channel, ExitCode: null);
        }
        catch (JsonException ex)
        {
            interactionService.DisplayError(ex.Message);
            return (ConfiguredChannel: null, ExitCode: CliExitCodes.FailedToLoadConfiguration);
        }
    }

    public static (string FriendlyName, NuGetPackage Package, PackageChannel Channel) GenerateFriendlyName((NuGetPackage Package, PackageChannel Channel) packageWithChannel)
    {
        var packageId = packageWithChannel.Package.Id.Replace("Aspire.Hosting.", "", StringComparison.OrdinalIgnoreCase);
        var friendlyName = packageId.Replace('.', '-').ToLowerInvariant();

        return (friendlyName, packageWithChannel.Package, packageWithChannel.Channel);
    }

    public static IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore)> GetIntegrationSearchMatches(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string searchTerm)
    {
        return packages
            .Select(p => (p.FriendlyName, p.Package, p.Channel, SearchScore: GetIntegrationSearchScore(searchTerm, p)))
            .Where(p => p.SearchScore > FuzzyMatchThreshold)
            .OrderByDescending(p => p.SearchScore)
            .ThenBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase);
    }

    public static (string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore) SelectPreferredIntegrationPackage(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore)> packages)
    {
        return packages
            .OrderByDescending(p => p.Channel.Type is PackageChannelType.Implicit)
            .ThenByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer)
            .First();
    }

    private static double GetIntegrationSearchScore(string searchTerm, (string FriendlyName, NuGetPackage Package, PackageChannel Channel) package)
    {
        return Math.Max(
            StringUtils.CalculateFuzzyScore(searchTerm, package.FriendlyName),
            StringUtils.CalculateFuzzyScore(searchTerm, package.Package.Id));
    }

    private async Task<IEnumerable<NuGetPackage>> FilterThirdPartyIntegrationPackagesAsync(PackageChannel channel, DirectoryInfo workingDirectory, IEnumerable<NuGetPackage> packageCandidates, CancellationToken cancellationToken)
    {
        var candidates = packageCandidates
            .Where(static package => !HostingIntegrationMetadata.IsBuiltInHostingPackageId(package.Id) &&
                !HostingIntegrationMetadata.IsKnownNonHostingAspirePackageId(package.Id))
            .DistinctBy(static package => $"{package.Id}\0{package.Version}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var verifiedPackages = new NuGetPackage?[candidates.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxThirdPartyVerificationConcurrency
            },
            async (index, ct) =>
            {
                var package = candidates[index];
                if (await IsVerifiedThirdPartyHostingPackageAsync(channel, workingDirectory, package.Id, package.Version, ct))
                {
                    verifiedPackages[index] = package;
                }
            });

        return verifiedPackages.OfType<NuGetPackage>();
    }

    private async Task<bool> IsVerifiedThirdPartyHostingPackageAsync(PackageChannel channel, DirectoryInfo workingDirectory, string packageId, string? packageVersion, CancellationToken cancellationToken)
    {
        return await packageTagMetadataService.HasAnyDependencyAsync(channel, workingDirectory, packageId, packageVersion, HostingIntegrationMetadata.HostingDependencyPackageIds, cancellationToken);
    }

    private async Task<PackageChannel[]> GetApplicableChannelsAsync(DirectoryInfo workingDirectory, string? configuredChannel, IntegrationDiscoveryScope discoveryScope, CancellationToken cancellationToken, string? requestedSource = null)
    {
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken);

        if (!string.IsNullOrEmpty(configuredChannel))
        {
            allChannels = allChannels.Where(c => string.Equals(c.Name, configuredChannel, StringComparison.OrdinalIgnoreCase));
        }

        var hasHives = executionContext.GetPrHiveCount() > 0;
        var channels = (hasHives || !string.IsNullOrEmpty(configuredChannel)
            ? allChannels
            : allChannels.Where(c => c.Type is PackageChannelType.Implicit ||
                string.Equals(c.Name, PackageChannelNames.Stable, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var configuredThirdPartyChannels = GetConfiguredThirdPartyChannels(workingDirectory, configuredChannel, discoveryScope);
        var requestedSourceChannel = CreateRequestedSourceChannel(requestedSource);
        return requestedSourceChannel is null
            ? [.. channels, .. configuredThirdPartyChannels]
            : [.. channels, .. configuredThirdPartyChannels, requestedSourceChannel];
    }

    private PackageChannel? CreateRequestedSourceChannel(string? requestedSource)
    {
        if (string.IsNullOrWhiteSpace(requestedSource))
        {
            return null;
        }

        return PackageChannel.CreateExplicitChannel(
            RequestedSourceChannelName,
            PackageChannelQuality.Both,
            [new PackageMapping(PackageMapping.AllPackages, requestedSource)],
            nuGetPackageCache);
    }

    private PackageChannel[] GetConfiguredThirdPartyChannels(DirectoryInfo workingDirectory, string? configuredChannel, IntegrationDiscoveryScope discoveryScope)
    {
        if (discoveryScope is IntegrationDiscoveryScope.Official ||
            (!string.IsNullOrEmpty(configuredChannel) && !string.Equals(configuredChannel, ThirdPartyChannelName, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var feeds = IntegrationDiscoveryScopeHelpers.GetConfiguredThirdPartyFeeds(workingDirectory);
        return feeds.Select((feed, index) => PackageChannel.CreateExplicitChannel(
            feeds.Length == 1 ? ThirdPartyChannelName : $"{ThirdPartyChannelName}-{index + 1}",
            PackageChannelQuality.Stable,
            [new PackageMapping(PackageMapping.AllPackages, feed)],
            nuGetPackageCache))
            .ToArray();
    }
}
