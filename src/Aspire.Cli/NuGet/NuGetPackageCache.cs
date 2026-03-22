// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using System.Globalization;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.NuGet;

internal interface INuGetPackageCache
{
    Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken);
}

internal sealed class NuGetPackageCache(IDotNetCliRunner cliRunner, IMemoryCache memoryCache, AspireCliTelemetry telemetry, IFeatures features) : INuGetPackageCache
{
    private const int SearchPageSize = 1000;
    
    // List of deprecated packages that should be filtered by default
    private static readonly HashSet<string> s_deprecatedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aspire.Hosting.Dapr"
    };

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        var nuGetConfigHashSuffix = nugetConfigFile is not null ? await ComputeNuGetConfigHashSuffixAsync(nugetConfigFile, cancellationToken) : string.Empty;
        var key = $"TemplatePackages-{workingDirectory.FullName}-{prerelease}-{nuGetConfigHashSuffix}";

        var packages = await memoryCache.GetOrCreateAsync(key, async (entry) =>
        {
            var packages = await GetPackagesAsync(workingDirectory, "Aspire.ProjectTemplates", null, prerelease, nugetConfigFile, true, cancellationToken);
            return packages.Where(p => p.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));

        }) ?? throw new NuGetPackageCacheException(ErrorStrings.FailedToRetrieveCachedTemplatePackages);

        return packages;
    }

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        return await GetPackagesAsync(workingDirectory, "Aspire.Hosting", null, prerelease, nugetConfigFile, true, cancellationToken);
    }

    public async Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        var nuGetConfigHashSuffix = nugetConfigFile is not null ? await ComputeNuGetConfigHashSuffixAsync(nugetConfigFile, cancellationToken) : string.Empty;
        var key = $"CliPackages-{workingDirectory.FullName}-{prerelease}-{nuGetConfigHashSuffix}";

        var packages = await memoryCache.GetOrCreateAsync(key, async (entry) =>
        {
            // Set cache expiration to 1 hour for CLI updates
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var packages = await GetPackagesAsync(workingDirectory, "Aspire.Cli", null, prerelease, nugetConfigFile, false, cancellationToken);
            return packages.Where(p => p.Id.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase));
        }) ?? [];

        return packages;
    }

    private static async Task<string> ComputeNuGetConfigHashSuffixAsync(FileInfo nugetConfigFile, CancellationToken cancellationToken)
    {
        using var stream = nugetConfigFile.OpenRead();
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes);
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string query, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var collectedPackages = new List<NuGetPackage>();
        var skip = 0;

        bool continueFetching;
        do
        {
            // This search should pick up Aspire.Hosting.* and CommunityToolkit.Aspire.Hosting.*
            var result = await cliRunner.SearchPackagesAsync(
                workingDirectory,
                query,
                prerelease,
                SearchPageSize,
                skip,
                nugetConfigFile,
                useCache, // Pass through the useCache parameter
                new DotNetCliRunnerInvocationOptions { SuppressLogging = true },
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
        var effectiveFilter = (NuGetPackage p) => 
        {
            if (filter is not null)
            {
                return filter(p.Id);
            }

            var isOfficialPackage = IsOfficialOrCommunityToolkitPackage(p.Id);
            
            // Apply deprecated package filter unless the user wants to show deprecated packages
            if (isOfficialPackage && !features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false))
            {
                return !s_deprecatedPackages.Contains(p.Id);
            }

            return isOfficialPackage;
        };
        
        return collectedPackages.Where(effectiveFilter);

        static bool IsOfficialOrCommunityToolkitPackage(string packageName)
        {
            var isHostingOrCommunityToolkitNamespaced = packageName.StartsWith("Aspire.Hosting.", StringComparison.Ordinal) ||
                   packageName.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparison.Ordinal) ||
                   packageName.Equals("Aspire.ProjectTemplates", StringComparison.Ordinal) ||
                   packageName.Equals("Aspire.Cli", StringComparison.Ordinal);

            var isExcluded = packageName.StartsWith("Aspire.Hosting.AppHost") ||
                             packageName.StartsWith("Aspire.Hosting.Sdk") ||
                             packageName.StartsWith("Aspire.Hosting.Orchestration") ||
                             packageName.StartsWith("Aspire.Hosting.Testing") ||
                             packageName.StartsWith("Aspire.Hosting.Msi");

            return isHostingOrCommunityToolkitNamespaced && !isExcluded;
        }
    }
}

internal sealed class NuGetPackageCacheException(string message) : Exception(message)
{
}
