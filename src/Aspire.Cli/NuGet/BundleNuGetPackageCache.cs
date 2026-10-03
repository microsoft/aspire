// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.NuGet;

/// <summary>
/// NuGet package cache implementation for bundled CLIs, which cannot rely on the .NET SDK's
/// <c>dotnet package search</c> command.
/// </summary>
internal sealed class BundleNuGetPackageCache(
    INuGetClient nuGetClient,
    ILogger<BundleNuGetPackageCache> logger,
    IFeatures features,
    NuGetInvocationConfigurationSource configurationSource) : INuGetPackageCache
{
    // The aspire-managed helper was always invoked with --take 1000.
    private const int SearchTake = 1000;

    public Task<NuGetPackageSearchConfiguration> CreateAmbientOverlayAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? channelMappings,
        CancellationToken cancellationToken)
        => configurationSource.CreateAmbientOverlayAsync(
            workingDirectory,
            channelMappings,
            cancellationToken);

    public Task<NuGetPackageSearchConfiguration> CreateStandaloneAsync(
        DirectoryInfo workingDirectory,
        PackageMapping[] mappings)
        => NuGetInvocationConfigurationSource.CreateStandaloneAsync(workingDirectory, mappings);

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            configuration,
            "Aspire.ProjectTemplates",
            prerelease,
            cancellationToken).ConfigureAwait(false);

        return packages.Where(package => package.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            configuration,
            "Aspire.Hosting",
            prerelease,
            cancellationToken).ConfigureAwait(false);

        return FilterPackages(packages, filter: null);
    }

    public async Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            configuration,
            "Aspire.Cli",
            prerelease,
            cancellationToken).ConfigureAwait(false);

        return packages.Where(package => package.Id.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        string packageId,
        Func<string, bool>? filter,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            configuration,
            packageId,
            prerelease,
            cancellationToken).ConfigureAwait(false);

        return FilterPackages(packages, filter);
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(
        NuGetPackageSearchConfiguration configuration,
        string exactPackageId,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var results = await SearchClientAsync(
            configuration,
            exactPackageId,
            prerelease,
            cancellationToken).ConfigureAwait(false);

        // The helper had no exact-match mode. The CLI ran an ordinary search for the package ID and expanded the
        // versions of the one result whose ID matched it exactly, including its casing.
        var exactMatch = results.FirstOrDefault(package => package.Id.Equals(exactPackageId, StringComparison.Ordinal));
        if (exactMatch is null)
        {
            return [];
        }

        return exactMatch.AllVersions.Select(version => new NuGetPackage
        {
            Id = exactMatch.Id,
            Version = version,
            Source = exactMatch.Source
        }).ToList();
    }

    private async Task<List<NuGetPackage>> SearchAsync(
        NuGetPackageSearchConfiguration configuration,
        string query,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var results = await SearchClientAsync(
            configuration,
            query,
            prerelease,
            cancellationToken).ConfigureAwait(false);

        return results.Select(package => new NuGetPackage
        {
            Id = package.Id,
            Version = package.Version,
            Source = package.Source
        }).ToList();
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> SearchClientAsync(
        NuGetPackageSearchConfiguration configuration,
        string query,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nuGetClient.SearchAsync(
                query,
                prerelease,
                SearchTake,
                explicitSources: [],
                configuration.ExplicitConfigFile?.FullName,
                configuration.EffectiveWorkingDirectory.FullName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NuGetOperationException ex)
        {
            logger.LogError("NuGet search failed");
            logger.LogError("NuGet search stderr: {Error}", ex.Output);

            // The helper exited with code 1 for every search failure, and this is the message the CLI reported for it.
            throw new NuGetPackageCacheException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.FailedToSearchForPackages, 1),
                ex);
        }
    }

    private IEnumerable<NuGetPackage> FilterPackages(
        IEnumerable<NuGetPackage> packages,
        Func<string, bool>? filter)
    {
        return filter is not null
            ? packages.Where(package => filter(package.Id))
            : FilterDeprecatedPackages(packages.Where(package => PackageIdFilters.IsOfficialOrCommunityToolkitPackage(package.Id)));
    }

    private IEnumerable<NuGetPackage> FilterDeprecatedPackages(IEnumerable<NuGetPackage> packages)
    {
        return features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false)
            ? packages
            : packages.Where(package => !DeprecatedPackages.IsDeprecated(package.Id));
    }

}
