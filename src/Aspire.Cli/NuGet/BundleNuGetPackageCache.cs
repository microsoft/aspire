// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
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
    BundleNuGetService nuGetService) : INuGetPackageCache
{
    // The aspire-managed helper was always invoked with --take 1000.
    private const int SearchTake = 1000;

    public Task<NuGetPackageOperationConfiguration> CreateChannelConfigurationAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? channelMappings,
        CancellationToken cancellationToken)
        => nuGetService.CreatePackageOperationConfigurationAsync(
            workingDirectory,
            channelMappings,
            restrictToSelectedSources: false,
            cancellationToken);

    public Task<NuGetPackageOperationConfiguration> CreateSourceRestrictedConfigurationAsync(
        DirectoryInfo workingDirectory,
        PackageMapping[] mappings,
        CancellationToken cancellationToken)
        => nuGetService.CreatePackageOperationConfigurationAsync(workingDirectory, mappings, restrictToSelectedSources: true, cancellationToken);

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(
        NuGetPackageOperationConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            configuration,
            "Aspire.ProjectTemplates",
            prerelease,
            SearchTake,
            cancellationToken).ConfigureAwait(false);

        return packages.Packages.Where(package => package.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));
    }

    public Task<PackageSearchResult<NuGetPackage>> GetIntegrationPackagesAsync(
        NuGetPackageOperationConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
        => GetPackagesAsync(configuration, "Aspire.Hosting", filter: null, prerelease, useCache: true, cancellationToken);

    public async Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(
        NuGetPackageOperationConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            configuration,
            "Aspire.Cli",
            prerelease,
            SearchTake,
            cancellationToken).ConfigureAwait(false);

        return packages.Packages.Where(package => package.Id.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PackageSearchResult<NuGetPackage>> GetPackagesAsync(
        NuGetPackageOperationConfiguration configuration,
        string packageId,
        Func<string, bool>? filter,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var displayQuery = PackageSourceRedactor.RedactForDisplay(packageId);
        logger.LogDebug("Searching NuGet packages for query '{Query}' (prerelease: {Prerelease}).", displayQuery, prerelease);

        // Allowed integration patterns are applied after searching, so matching packages
        // must not be lost behind unrelated results on the first page.
        var packages = await SearchAsync(
            configuration,
            packageId,
            prerelease,
            filter is null ? SearchTake : int.MaxValue,
            cancellationToken).ConfigureAwait(false);

        var matches = FilterPackages(packages.Packages, filter).ToArray();
        logger.LogDebug(
            "NuGet search for query '{Query}' returned {ReturnedCount} packages; {MatchedCount} matched in {ElapsedMilliseconds} ms.",
            displayQuery, packages.Packages.Count, matches.Length, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

        return new(matches, packages.LimitReached, packages.FailedSources);
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(
        NuGetPackageOperationConfiguration configuration,
        string exactPackageId,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var results = await SearchClientAsync(
            configuration,
            exactPackageId,
            prerelease,
            SearchTake,
            cancellationToken).ConfigureAwait(false);

        // The helper had no exact-match mode. The CLI ran an ordinary search for the package ID and expanded the
        // versions of the one result whose ID matched it exactly, including its casing.
        var exactMatch = results.Packages.FirstOrDefault(package => package.Id.Equals(exactPackageId, StringComparison.Ordinal));
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

    private async Task<PackageSearchResult<NuGetPackage>> SearchAsync(
        NuGetPackageOperationConfiguration configuration,
        string query,
        bool prerelease,
        int take,
        CancellationToken cancellationToken)
    {
        var results = await SearchClientAsync(
            configuration,
            query,
            prerelease,
            take,
            cancellationToken).ConfigureAwait(false);

        var packages = results.Packages.Select(package => new NuGetPackage
        {
            Id = package.Id,
            Version = package.Version,
            Source = package.Source
        }).ToList();

        return new(packages, results.LimitReached, results.FailedSources);
    }

    private async Task<PackageSearchResult<NuGetSearchResult>> SearchClientAsync(
        NuGetPackageOperationConfiguration configuration,
        string query,
        bool prerelease,
        int take,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nuGetClient.SearchAsync(
                query,
                prerelease,
                take,
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
