// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Commands;

internal sealed class IntegrationPackageSearchService(
    IPackagingService packagingService,
    IProjectLocator projectLocator,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IAppHostProjectFactory projectFactory)
{
    private const double FuzzyMatchThreshold = 0.3;

    public async Task<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>> GetIntegrationPackagesWithChannelsAsync(DirectoryInfo workingDirectory, string? configuredChannel, CancellationToken cancellationToken)
    {
        // `configuredChannel` (from a polyglot apphost's aspire.config.json) is forwarded
        // as `requestedChannelName` so PackagingService can synthesize the staging channel
        // for out-of-tree apphosts whose directory wasn't picked up by
        // ConfigurationHelper.RegisterSettingsFiles.
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken, configuredChannel);

        // Channels included in the search:
        //   * Implicit channel: always.
        //   * PR hives: always when present.
        //   * Apphost-pinned explicit channel: only the channel whose name matches
        //     `configuredChannel` (no other explicit channels).
        //
        // Why the explicit channel set is scoped to the pinned channel only — NOT the full
        // explicit set — when an apphost pins a channel:
        //   - The full set produces duplicate matches in `aspire add` (e.g. a staging-pinned
        //     polyglot apphost would see Aspire.Hosting.Foundry surfaced by both the synthesized
        //     'staging' channel AND the unrelated 'daily' channel, with the daily entry
        //     pointing at a stale version like 13.3.5 that's not aligned with the project's
        //     pinned feed). C# apphosts never had this problem: GetConfiguredChannel returns
        //     null for C# (line ~99) and `configuredChannel` stays empty, so only Implicit is
        //     queried and the project-local NuGet.config scopes the search to the right feed.
        //   - Implicit MUST stay in the set so prerelease-only packages remain reachable when
        //     the project pin is Stable-quality. This is the #17724 / #17725 guarantee:
        //     pre-fix, the search was narrowed to *only* the pinned channel, and a Stable pin
        //     made prerelease packages (Aspire.Hosting.Foundry) invisible because Quality.Stable
        //     channels only issue prerelease=false queries. Keeping Implicit (Quality.Both)
        //     preserves prerelease visibility.
        //   - As of #17768, all polyglot starters write a project-local NuGet.config that pins
        //     Aspire.* to the resolved channel's feed via PSM, so Implicit's `dotnet package
        //     search` from the apphost cwd returns packages from the same feed the pinned
        //     explicit channel would return — matching the C# behavior end-to-end.
        var hasHives = executionContext.GetHiveCount() > 0;
        var channels = hasHives
            ? allChannels
            : allChannels.Where(c =>
                c.Type is PackageChannelType.Implicit ||
                (!string.IsNullOrEmpty(configuredChannel) &&
                 string.Equals(c.Name, configuredChannel, StringComparisons.ChannelName)));

        var packages = new List<(NuGetPackage Package, PackageChannel Channel)>();
        var packagesLock = new object();

        await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
        {
            var integrationPackages = await channel.GetIntegrationPackagesAsync(
                workingDirectory: workingDirectory,
                cancellationToken: ct);
            lock (packagesLock)
            {
                packages.AddRange(integrationPackages.Select(p => (p, channel)));
            }
        });

        return packages;
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
            .ThenByDescending(p => p.FriendlyName, new CommunityToolkitFirstComparer());
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
}
