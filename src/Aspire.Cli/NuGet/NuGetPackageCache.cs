// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.NuGet;

internal interface INuGetPackageCache
{
    Task<NuGetPackageSearchConfiguration> CreateAmbientOverlayAsync(DirectoryInfo workingDirectory, IReadOnlyList<PackageMapping>? channelMappings, CancellationToken cancellationToken);
    Task<NuGetPackageSearchConfiguration> CreateStandaloneAsync(DirectoryInfo workingDirectory, PackageMapping[] mappings);
    Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(NuGetPackageSearchConfiguration configuration, bool prerelease, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(NuGetPackageSearchConfiguration configuration, bool prerelease, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(NuGetPackageSearchConfiguration configuration, bool prerelease, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetPackagesAsync(NuGetPackageSearchConfiguration configuration, string packageId, Func<string, bool>? filter, bool prerelease, bool useCache, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(NuGetPackageSearchConfiguration configuration, string exactPackageId, bool prerelease, bool useCache, CancellationToken cancellationToken);

    Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetTemplatePackagesAsync(NuGetPackageSearchConfiguration.Existing(workingDirectory, nugetConfigFile), prerelease, cancellationToken);

    Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetIntegrationPackagesAsync(NuGetPackageSearchConfiguration.Existing(workingDirectory, nugetConfigFile), prerelease, cancellationToken);

    Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetCliPackagesAsync(NuGetPackageSearchConfiguration.Existing(workingDirectory, nugetConfigFile), prerelease, cancellationToken);

    Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        => GetPackagesAsync(NuGetPackageSearchConfiguration.Existing(workingDirectory, nugetConfigFile), packageId, filter, prerelease, useCache, cancellationToken);

    Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(DirectoryInfo workingDirectory, string exactPackageId, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        => GetPackageVersionsAsync(NuGetPackageSearchConfiguration.Existing(workingDirectory, nugetConfigFile), exactPackageId, prerelease, useCache, cancellationToken);
}

/// <summary>
/// Packages that have been superseded and should be hidden from integration listings by default.
/// </summary>
internal static class DeprecatedPackages
{
    private static readonly FrozenSet<string> s_all = new[]
    {
        "Aspire.Hosting.Dapr",
        "Aspire.Hosting.GitHub.Models",
        "Aspire.Hosting.NodeJs"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsDeprecated(string packageId) => s_all.Contains(packageId);
}

internal static class PackageIdFilters
{
    public static bool IsOfficialOrCommunityToolkitPackage(string packageId)
    {
        var isHostingOrCommunityToolkitNamespaced = packageId.StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparison.OrdinalIgnoreCase) ||
            packageId.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase) ||
            packageId.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase);

        return isHostingOrCommunityToolkitNamespaced && !IsExcludedHostingPackage(packageId);
    }

    public static bool IsIntegrationPackageId(string packageId)
    {
        var isHostingOrCommunityToolkitNamespaced = packageId.StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparison.OrdinalIgnoreCase);

        return isHostingOrCommunityToolkitNamespaced && !IsExcludedHostingPackage(packageId);
    }

    private static bool IsExcludedHostingPackage(string packageId)
    {
        return packageId.StartsWith("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Sdk", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Orchestration", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Testing", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Msi", StringComparison.OrdinalIgnoreCase) ||
            packageId.Equals("Aspire.Hosting.Integration.Analyzers", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class NuGetPackageCache(
    IDotNetCliRunner cliRunner,
    IMemoryCache memoryCache,
    AspireCliTelemetry telemetry,
    IFeatures features,
    NuGetInvocationConfigurationSource configurationSource) : INuGetPackageCache
{
    private const int SearchPageSize = 1000;

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
        var key = $"TemplatePackages-{configuration.OriginalWorkingDirectory.FullName}-{prerelease}-{configuration.CacheIdentity}";

        var packages = await memoryCache.GetOrCreateAsync(key, async (entry) =>
        {
            var packages = await GetPackagesAsync(configuration, "Aspire.ProjectTemplates", null, prerelease, true, cancellationToken);
            return packages.Where(p => p.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));

        }) ?? throw new NuGetPackageCacheException(ErrorStrings.FailedToRetrieveCachedTemplatePackages);

        return packages;
    }

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        return await GetPackagesAsync(configuration, "Aspire.Hosting", null, prerelease, true, cancellationToken);
    }

    public async Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        bool prerelease,
        CancellationToken cancellationToken)
    {
        var key = $"CliPackages-{configuration.OriginalWorkingDirectory.FullName}-{prerelease}-{configuration.CacheIdentity}";

        var packages = await memoryCache.GetOrCreateAsync(key, async (entry) =>
        {
            // Set cache expiration to 1 hour for CLI updates
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var packages = await GetPackagesAsync(configuration, "Aspire.Cli", null, prerelease, false, cancellationToken);
            return packages.Where(p => p.Id.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase));
        }) ?? [];

        return packages;
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackagesAsync(
        NuGetPackageSearchConfiguration configuration,
        string query,
        Func<string, bool>? filter,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var collectedPackages = new List<NuGetPackage>();
        var skip = 0;

        bool continueFetching;
        do
        {
            // This search should pick up Aspire.Hosting.* and CommunityToolkit.Aspire.Hosting.*
            var result = await cliRunner.SearchPackagesAsync(
                configuration.EffectiveWorkingDirectory,
                query,
                exactMatch: false,
                prerelease,
                SearchPageSize,
                skip,
                configuration.ExplicitConfigFile,
                useCache, // Pass through the useCache parameter
                new ProcessInvocationOptions { SuppressLogging = true },
                cancellationToken
                );

            if (result.ExitCode != 0)
            {
                throw new NuGetPackageCacheException(string.Format(CultureInfo.CurrentCulture, ErrorStrings.FailedToSearchForPackages, result.ExitCode));
            }
            else
            {
                if (result.Packages?.Length > 0)
                {
                    collectedPackages.AddRange(result.Packages);
                }

                if (result.Packages?.Length < SearchPageSize)
                {
                    continueFetching = false;
                }
                else
                {
                    continueFetching = true;
                    skip += SearchPageSize;
                }
            }
        } while (continueFetching);

        // If no specific filter is specified we use the fallback filter which is useful in most circumstances
        // other that aspire update which really needs to see all the packages to work effectively.
        var showDeprecatedPackages = features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false);
        var effectiveFilter = (NuGetPackage p) =>
        {
            if (filter is not null)
            {
                return filter(p.Id);
            }

            var isOfficialPackage = PackageIdFilters.IsOfficialOrCommunityToolkitPackage(p.Id);

            // Apply deprecated package filter unless the user wants to show deprecated packages
            if (isOfficialPackage && !showDeprecatedPackages)
            {
                return !DeprecatedPackages.IsDeprecated(p.Id);
            }

            return isOfficialPackage;
        };

        return collectedPackages.Where(effectiveFilter);
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(
        NuGetPackageSearchConfiguration configuration,
        string exactPackageId,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var collectedPackages = new List<NuGetPackage>();

        var result = await cliRunner.SearchPackagesAsync(
                configuration.EffectiveWorkingDirectory,
                exactPackageId,
                exactMatch: true,
                prerelease,
                take: 0,
                skip: 0, // skip and take parameters are ignored when exactMatch is true
                configuration.ExplicitConfigFile,
                useCache, // Pass through the useCache parameter
                new ProcessInvocationOptions { SuppressLogging = true },
                cancellationToken
                );

        if (result.ExitCode != 0)
        {
            throw new NuGetPackageCacheException(string.Format(CultureInfo.CurrentCulture, ErrorStrings.FailedToSearchForPackages, result.ExitCode));
        }

        if (result.Packages?.Length > 0)
        {
            collectedPackages.AddRange(result.Packages);
        }

        // If no specific filter is specified we use the fallback filter which is useful in most circumstances
        // other that aspire update which really needs to see all the packages to work effectively.
        var effectiveFilter = (NuGetPackage p) =>
        {
            // Apply deprecated package filter unless the user wants to show deprecated packages
            if (!features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false))
            {
                return !DeprecatedPackages.IsDeprecated(p.Id);
            }
            return true;
        };

        return collectedPackages.Where(effectiveFilter);
    }

}

internal sealed class NuGetPackageCacheException : Exception
{
    public NuGetPackageCacheException(string message)
        : base(message)
    {
    }

    public NuGetPackageCacheException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
