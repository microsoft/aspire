// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Integrations;
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
    IAppHostProjectFactory projectFactory,
    IIntegrationResolver integrationResolver)
{
    private const double FuzzyMatchThreshold = 0.3;

    public async Task<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>> GetIntegrationPackagesWithChannelsAsync(DirectoryInfo workingDirectory, string? configuredChannel, CancellationToken cancellationToken)
    {
        var candidates = await GetIntegrationPackageCandidatesAsync(workingDirectory, configuredChannel, cancellationToken);
        return candidates.Select(static candidate => (candidate.Package, candidate.Channel));
    }

    public async Task<IEnumerable<IntegrationPackageCandidate>> GetIntegrationPackageCandidatesAsync(DirectoryInfo workingDirectory, string? configuredChannel, CancellationToken cancellationToken)
    {
        // `configuredChannel` (from a polyglot apphost's aspire.config.json) is forwarded
        // as `requestedChannelName` so PackagingService can synthesize the staging channel
        // for out-of-tree apphosts whose directory wasn't picked up by
        // ConfigurationHelper.RegisterSettingsFiles.
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken, configuredChannel);

        // Channels included in the search:
        //   * Implicit channel: always.
        //   * Explicit channels (stable, daily, staging, custom): when PR hives exist OR the
        //     apphost has pinned an explicit channel via aspire.config.json.
        //
        // What this method MUST NOT do is narrow the explicit channel set to just the pinned
        // channel. That was the root cause of https://github.com/microsoft/aspire/issues/17724
        // and https://github.com/microsoft/aspire/issues/17725: a TS apphost pinned to a
        // Quality.Stable channel ended up with prerelease=false queries everywhere and
        // prerelease-only packages (e.g. Aspire.Hosting.Foundry) became invisible. The implicit
        // channel (Quality.Both) must always participate so prerelease packages are reachable
        // even when the explicit pin is Stable-quality.
        // An ASPIRE_CLI_PACKAGES / sidecar `packages` override deliberately points Aspire.*
        // resolution at a local directory (used to emulate a released/staging build from locally
        // built packages). Treat it like a hive so the synthesized local channel — named after the
        // emulated identity (stable/daily/staging), not a local-build name — participates in the
        // search instead of being filtered out, which would silently fall back to nuget.org.
        var hasHives = executionContext.GetHiveCount() > 0 || executionContext.IdentityPackagesDirectory is not null;
        var channels = hasHives || !string.IsNullOrEmpty(configuredChannel)
            ? allChannels
            : allChannels.Where(c => c.Type is PackageChannelType.Implicit);

        return await integrationResolver.GetPackageCandidatesAsync(
            new IntegrationIndexSourceContext(workingDirectory, channels.ToArray()),
            cancellationToken);
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
        return (IntegrationNameHelper.GenerateFriendlyName(packageWithChannel.Package.Id), packageWithChannel.Package, packageWithChannel.Channel);
    }

    public static IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore)> GetIntegrationSearchMatches(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string searchTerm)
    {
        return packages
            .Select(p => (p.FriendlyName, p.Package, p.Channel, SearchScore: GetIntegrationSearchScore(searchTerm, p)))
            .Where(p => p.SearchScore > FuzzyMatchThreshold)
            .OrderByDescending(p => p.SearchScore)
            .ThenByDescending(p => p.FriendlyName, new CommunityToolkitFirstComparer());
    }

    public static IEnumerable<(IntegrationPackageCandidate Candidate, double SearchScore)> GetIntegrationSearchMatches(IEnumerable<IntegrationPackageCandidate> candidates, string searchTerm)
    {
        return candidates
            .Select(candidate => (Candidate: candidate, SearchScore: candidate.GetSearchScore(searchTerm)))
            .Where(candidate => candidate.SearchScore > FuzzyMatchThreshold)
            .OrderByDescending(candidate => candidate.SearchScore)
            .ThenByDescending(candidate => candidate.Candidate.Name, new CommunityToolkitFirstComparer());
    }

    public static (string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore) SelectPreferredIntegrationPackage(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore)> packages)
    {
        return packages
            .OrderByDescending(p => p.Channel.Type is PackageChannelType.Implicit)
            .ThenByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer)
            .First();
    }

    public static IntegrationPackageCandidate SelectPreferredIntegrationPackage(IEnumerable<IntegrationPackageCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.Channel.Type is PackageChannelType.Implicit)
            .ThenByDescending(candidate => SemVersion.Parse(candidate.Package.Version), SemVersion.PrecedenceComparer)
            .First();
    }

    private static double GetIntegrationSearchScore(string searchTerm, (string FriendlyName, NuGetPackage Package, PackageChannel Channel) package)
    {
        return Math.Max(
            StringUtils.CalculateFuzzyScore(searchTerm, package.FriendlyName),
            StringUtils.CalculateFuzzyScore(searchTerm, package.Package.Id));
    }
}
