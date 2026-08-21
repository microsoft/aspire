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
    IAppHostProjectFactory projectFactory,
    IConfigurationService configurationService)
{
    private const double FuzzyMatchThreshold = 0.3;

    public async Task<IEnumerable<(NuGetPackage Package, PackageChannel Channel)>> GetIntegrationPackagesWithChannelsAsync(DirectoryInfo workingDirectory, string? configuredChannel, string? source, CancellationToken cancellationToken)
    {
        var channels = await GetSearchChannelsAsync(workingDirectory, configuredChannel, source, cancellationToken);

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

    /// <summary>
    /// Searches the same channels as <see cref="GetIntegrationPackagesWithChannelsAsync"/> and, in the same
    /// pass, resolves the union of integration package IDs that are marked polyglot-compatible (carry the
    /// <c>polyglot</c> NuGet tag). Used by <c>aspire add</c> and integration discovery to hide integrations a
    /// non-C# AppHost cannot consume unless <c>--all</c> is passed.
    /// </summary>
    /// <remarks>
    /// Resolving both lists together avoids re-resolving the channel set and lets each channel's integration
    /// search and its <c>tags:polyglot</c> lookup run concurrently, rather than as two serial discovery passes.
    /// </remarks>
    public async Task<(IReadOnlyList<(NuGetPackage Package, PackageChannel Channel)> Packages, IReadOnlySet<string> PolyglotCompatibleIds)> GetIntegrationPackagesWithPolyglotCompatibilityAsync(DirectoryInfo workingDirectory, string? configuredChannel, string? source, CancellationToken cancellationToken)
    {
        var channels = await GetSearchChannelsAsync(workingDirectory, configuredChannel, source, cancellationToken);

        var packages = new List<(NuGetPackage Package, PackageChannel Channel)>();
        var polyglotIds = new HashSet<string>(StringComparers.NuGetPackageId);
        var gate = new object();

        await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
        {
            // Resolve the integration list and the polyglot allow-list for this channel concurrently so the
            // compatibility lookup runs alongside the integration search instead of as a second serial pass.
            var integrationPackagesTask = channel.GetIntegrationPackagesAsync(workingDirectory: workingDirectory, cancellationToken: ct);
            var polyglotIdsTask = channel.GetPolyglotCompatiblePackageIdsAsync(workingDirectory: workingDirectory, cancellationToken: ct);
            await Task.WhenAll(integrationPackagesTask, polyglotIdsTask);

            lock (gate)
            {
                packages.AddRange(integrationPackagesTask.Result.Select(p => (p, channel)));
                polyglotIds.UnionWith(polyglotIdsTask.Result);
            }
        });

        return (packages, polyglotIds);
    }

    private async Task<IEnumerable<PackageChannel>> GetSearchChannelsAsync(DirectoryInfo workingDirectory, string? configuredChannel, string? source, CancellationToken cancellationToken)
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

        if (string.IsNullOrWhiteSpace(source))
        {
            return channels;
        }

        var resolvedSource = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workingDirectory);
        var mappings = PackageSourceOverrideMappings.CreateForTemplateOperations(resolvedSource);
        return channels.Select(channel => channel.WithMappings(mappings));
    }

    public async Task<(DirectoryInfo WorkingDirectory, string? ConfiguredChannel, string? ConfiguredSource, string? LanguageId, int? ExitCode)> GetPackageSearchContextAsync(
        FileInfo? passedAppHostProjectFile,
        string? invocationConfiguredSource,
        CancellationToken cancellationToken)
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
            return (
                executionContext.WorkingDirectory,
                ConfiguredChannel: null,
                ConfiguredSource: NormalizeSource(invocationConfiguredSource),
                LanguageId: null,
                ExitCode: null);
        }

        var project = projectFactory.GetProject(appHostProjectFile);
        var (configuredChannel, exitCode) = GetConfiguredChannel(appHostProjectFile, project);
        var configuredSource = await GetConfiguredNuGetSourceAsync(
            appHostProjectFile,
            appHostWasExplicitlyPassed: passedAppHostProjectFile is not null,
            invocationConfiguredSource,
            cancellationToken);
        return (appHostProjectFile.Directory!, configuredChannel, configuredSource, project.LanguageId, exitCode);
    }

    public async Task<string?> GetConfiguredNuGetSourceAsync(
        FileInfo appHostProjectFile,
        bool appHostWasExplicitlyPassed,
        string? invocationConfiguredSource,
        CancellationToken cancellationToken)
    {
        var selectingSource = await configurationService.GetConfigurationFromDirectoryWithOriginAsync(
            AspireConfigFile.NuGetSourceKey,
            executionContext.WorkingDirectory,
            cancellationToken: cancellationToken);
        var targetSource = await configurationService.GetConfigurationFromDirectoryWithOriginAsync(
            AspireConfigFile.NuGetSourceKey,
            appHostProjectFile.Directory!,
            cancellationToken: cancellationToken);
        var invocationSource = NormalizeSource(invocationConfiguredSource);

        if (appHostWasExplicitlyPassed)
        {
            if (targetSource is not null)
            {
                return ResolveSource(targetSource.Value, targetSource.BaseDirectory);
            }

            // Environment and command-host providers still outrank files, but a local
            // config from the invocation workspace must not leak into an explicit target.
            return invocationSource is not null && selectingSource is null
                    ? ResolveSource(invocationSource, executionContext.WorkingDirectory)
                    : null;
        }

        if (selectingSource is { IsGlobal: false })
        {
            return ResolveSource(selectingSource.Value, selectingSource.BaseDirectory);
        }

        if (invocationSource is not null && selectingSource is null)
        {
            return ResolveSource(invocationSource, executionContext.WorkingDirectory);
        }

        if (targetSource is { IsGlobal: false })
        {
            return ResolveSource(targetSource.Value, targetSource.BaseDirectory);
        }

        var globalSource = targetSource ?? selectingSource;
        return globalSource is null
            ? null
            : ResolveSource(globalSource.Value, globalSource.BaseDirectory);
    }

    private static string? NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? null : source;

    private static string ResolveSource(string source, DirectoryInfo workingDirectory)
        => PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workingDirectory);

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
