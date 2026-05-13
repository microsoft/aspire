// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using System.Collections.Frozen;
using System.Globalization;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.NuGet;

internal interface INuGetPackageCache
{
    Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken);
    Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(DirectoryInfo workingDirectory, string exactPackageId, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the latest stable and latest prerelease version for each id in <paramref name="packageIds"/>
    /// using a single batched lookup against all configured NuGet sources.
    /// Used by <c>aspire update</c> to avoid issuing one subprocess per package id × quality.
    /// Returned dictionary is keyed case-insensitively by id; ids with no matches across any source are
    /// represented by an entry whose <see cref="PackageLatestVersions.LatestStable"/> and
    /// <see cref="PackageLatestVersions.LatestPrerelease"/> are both <c>null</c>, or are absent entirely.
    /// </summary>
    Task<IReadOnlyDictionary<string, PackageLatestVersions>> GetLatestVersionsAsync(
        IEnumerable<string> packageIds,
        DirectoryInfo workingDirectory,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken);
}

/// <summary>
/// Latest known stable and prerelease versions for a single package id across all configured sources.
/// Either field may be <c>null</c> when no matching version exists on any configured source.
/// </summary>
internal sealed class PackageLatestVersions
{
    public NuGetPackage? LatestStable { get; init; }
    public NuGetPackage? LatestPrerelease { get; init; }
}

/// <summary>
/// Packages that have been superseded and should be hidden from integration listings by default.
/// </summary>
internal static class DeprecatedPackages
{
    private static readonly FrozenSet<string> s_all = new[]
    {
        "Aspire.Hosting.Dapr",
        "Aspire.Hosting.NodeJs"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsDeprecated(string packageId) => s_all.Contains(packageId);
}

internal sealed class NuGetPackageCache(IDotNetCliRunner cliRunner, IMemoryCache memoryCache, AspireCliTelemetry telemetry, IFeatures features) : INuGetPackageCache
{
    private const int SearchPageSize = 1000;

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
                exactMatch: false,
                prerelease,
                SearchPageSize,
                skip,
                nugetConfigFile,
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

            var isOfficialPackage = IsOfficialOrCommunityToolkitPackage(p.Id);
            
            // Apply deprecated package filter unless the user wants to show deprecated packages
            if (isOfficialPackage && !showDeprecatedPackages)
            {
                return !DeprecatedPackages.IsDeprecated(p.Id);
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

    public async Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(DirectoryInfo workingDirectory, string exactPackageId, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var collectedPackages = new List<NuGetPackage>();

        var result = await cliRunner.SearchPackagesAsync(
                workingDirectory,
                exactPackageId,
                exactMatch: true,
                prerelease,
                take: 0,
                skip: 0, // skip and take parameters are ignored when exactMatch is true
                nugetConfigFile,
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

    public async Task<IReadOnlyDictionary<string, PackageLatestVersions>> GetLatestVersionsAsync(
        IEnumerable<string> packageIds,
        DirectoryInfo workingDirectory,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        // SDK transport (no bundle) cannot batch in a single subprocess because `dotnet package search`
        // accepts only one query per invocation. Issue per-id × per-quality calls in parallel with a
        // small concurrency cap so users still get a meaningful speedup over today's serial loop without
        // accidentally fanning out to dozens of concurrent subprocesses on slow networks.
        var ids = packageIds
            .Where(static id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<string, PackageLatestVersions>(StringComparer.OrdinalIgnoreCase);
        }

        const int MaxParallelism = 4;
        using var throttle = new SemaphoreSlim(MaxParallelism);

        var results = new Dictionary<string, PackageLatestVersions>(StringComparer.OrdinalIgnoreCase);
        var resultsLock = new object();

        async Task ResolveAsync(string id)
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Run the stable + prerelease searches concurrently for the same id.
                var stableTask = GetPackagesAsync(workingDirectory, id, pid => string.Equals(pid, id, StringComparison.OrdinalIgnoreCase), prerelease: false, nugetConfigFile, useCache: true, cancellationToken);
                var prereleaseTask = GetPackagesAsync(workingDirectory, id, pid => string.Equals(pid, id, StringComparison.OrdinalIgnoreCase), prerelease: true, nugetConfigFile, useCache: true, cancellationToken);
                await Task.WhenAll(stableTask, prereleaseTask).ConfigureAwait(false);

                var latestStable = PickLatest(stableTask.Result, requirePrerelease: false);
                var latestPrerelease = PickLatest(prereleaseTask.Result, requirePrerelease: true);

                lock (resultsLock)
                {
                    results[id] = new PackageLatestVersions
                    {
                        LatestStable = latestStable,
                        LatestPrerelease = latestPrerelease
                    };
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        await Task.WhenAll(ids.Select(ResolveAsync)).ConfigureAwait(false);
        return results;
    }

    private static NuGetPackage? PickLatest(IEnumerable<NuGetPackage> packages, bool requirePrerelease)
    {
        return packages
            .Where(p => SemVersion.TryParse(p.Version, SemVersionStyles.Strict, out var sv) && (!requirePrerelease || sv.IsPrerelease))
            .OrderByDescending(p => SemVersion.Parse(p.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
            .FirstOrDefault();
    }
}

internal sealed class NuGetPackageCacheException(string message) : Exception(message)
{
}
