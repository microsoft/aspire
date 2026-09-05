// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Telemetry;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.NuGet;

/// <summary>
/// NuGet package cache implementation for bundled CLIs, which cannot rely on a .NET SDK.
/// </summary>
internal sealed class BundleNuGetPackageCache(
    INuGetClient nuGetClient,
    AspireCliTelemetry telemetry,
    IFeatures features) : INuGetPackageCache
{
    private const int SearchPageSize = 1000;

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var packages = await SearchAsync(
            workingDirectory,
            "Aspire.ProjectTemplates",
            exactMatch: false,
            prerelease,
            nugetConfigFile,
            useCache: true,
            cancellationToken).ConfigureAwait(false);
        return packages.Where(package => package.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));
    }

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        return GetPackagesAsync(
            workingDirectory,
            "Aspire.Hosting",
            filter: null,
            prerelease,
            nugetConfigFile,
            useCache: true,
            cancellationToken);
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
            exactMatch: false,
            prerelease,
            nugetConfigFile,
            useCache: false,
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
            exactMatch: false,
            prerelease,
            nugetConfigFile,
            useCache,
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
        var results = await nuGetClient.SearchAsync(
            exactPackageId,
            exactMatch: true,
            prerelease,
            SearchPageSize,
            useCache,
            explicitSources: [],
            nugetConfigFile?.FullName,
            workingDirectory.FullName,
            cancellationToken).ConfigureAwait(false);
        var packages = results
            .Where(package => package.Id.Equals(exactPackageId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(package => package.AllVersions.Select(version => new NuGetPackage
            {
                Id = package.Id,
                Version = version,
                Source = package.Source
            }))
            .DistinctBy(package => package.Version, StringComparer.OrdinalIgnoreCase);
        return FilterDeprecatedPackages(packages);
    }

    private async Task<IEnumerable<NuGetPackage>> SearchAsync(
        DirectoryInfo workingDirectory,
        string query,
        bool exactMatch,
        bool prerelease,
        FileInfo? nugetConfigFile,
        bool useCache,
        CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var results = await nuGetClient.SearchAsync(
            query,
            exactMatch,
            prerelease,
            SearchPageSize,
            useCache,
            explicitSources: [],
            nugetConfigFile?.FullName,
            workingDirectory.FullName,
            cancellationToken).ConfigureAwait(false);
        return results.Select(package => new NuGetPackage
        {
            Id = package.Id,
            Version = package.Version,
            Source = package.Source
        });
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
