// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
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
    IFeatures features) : INuGetPackageCache
{
    // The aspire-managed helper was always invoked with --take 1000.
    private const int SearchTake = 1000;

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            workingDirectory,
            "Aspire.ProjectTemplates",
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return packages.Where(package => package.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            workingDirectory,
            "Aspire.Hosting",
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return FilterPackages(packages, filter: null);
    }

    public async Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            workingDirectory,
            "Aspire.Cli",
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return packages.Where(package => package.Id.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackagesAsync(
        DirectoryInfo workingDirectory,
        string packageId,
        Func<string, bool>? filter,
        bool prerelease,
        FileInfo? nugetConfigFile,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            workingDirectory,
            packageId,
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return FilterPackages(packages, filter);
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(
        DirectoryInfo workingDirectory,
        string exactPackageId,
        bool prerelease,
        FileInfo? nugetConfigFile,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var results = await SearchClientAsync(
            workingDirectory,
            exactPackageId,
            prerelease,
            nugetConfigFile,
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
        DirectoryInfo workingDirectory,
        string query,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var results = await SearchClientAsync(
            workingDirectory,
            query,
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return results.Select(package => new NuGetPackage
        {
            Id = package.Id,
            Version = package.Version,
            Source = package.Source
        }).ToList();
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> SearchClientAsync(
        DirectoryInfo workingDirectory,
        string query,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nuGetClient.SearchAsync(
                query,
                prerelease,
                SearchTake,
                explicitSources: [],
                nugetConfigFile?.FullName,
                workingDirectory.FullName,
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
