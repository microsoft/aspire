// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Microsoft.Extensions.Logging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.NuGet;

/// <summary>
/// NuGet package cache implementation that uses the bundle's NuGetHelper tool
/// instead of the .NET SDK's `dotnet package search` command.
/// </summary>
internal sealed class BundleNuGetPackageCache : INuGetPackageCache
{
    private readonly IBundleService _bundleService;
    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly ILogger<BundleNuGetPackageCache> _logger;
    private readonly IFeatures _features;

    public BundleNuGetPackageCache(
        IBundleService bundleService,
        LayoutProcessRunner layoutProcessRunner,
        ILogger<BundleNuGetPackageCache> logger,
        IFeatures features)
    {
        _bundleService = bundleService;
        _layoutProcessRunner = layoutProcessRunner;
        _logger = logger;
        _features = features;
    }

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var packages = await SearchPackagesInternalAsync(
            workingDirectory,
            query: "Aspire.ProjectTemplates",
            exactMatch: false,
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return packages.Where(p => p.Id.Equals("Aspire.ProjectTemplates", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(
        DirectoryInfo workingDirectory,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var packages = await SearchPackagesInternalAsync(
            workingDirectory,
            query: "Aspire.Hosting",
            exactMatch: false,
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
        var packages = await SearchPackagesInternalAsync(
            workingDirectory,
            query: "Aspire.Cli",
            exactMatch: false,
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return packages.Where(p => p.Id.Equals("Aspire.Cli", StringComparison.OrdinalIgnoreCase));
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
        var packages = await SearchPackagesInternalAsync(
            workingDirectory,
            query: packageId,
            exactMatch: false,
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
        var packages = await SearchPackagesInternalAsync(
            workingDirectory,
            query: exactPackageId,
            exactMatch: true,
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        bool FilterExactIdMatch(string? id) => string.Equals(id, exactPackageId, StringComparison.Ordinal);
        return FilterPackages(packages, FilterExactIdMatch);
    }

    private async Task<IEnumerable<NuGetPackage>> SearchPackagesInternalAsync(
        DirectoryInfo workingDirectory,
        string query,
        bool exactMatch,
        bool prerelease,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        // Ensure the bundle is extracted and get the layout in a single call
        var layout = await _bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken).ConfigureAwait(false);
        if (layout is null)
        {
            throw new InvalidOperationException("Bundle layout not found. Cannot perform NuGet search in bundle mode.");
        }

        var managedPath = layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException("aspire-managed not found in layout.");
        }

        // Build arguments for NuGet search command (via aspire-managed nuget subcommand)
        var args = new List<string>
        {
            "nuget",
            "search",
            "--query", query,
            "--take", "1000",
            "--format", "json"
        };

        if (prerelease)
        {
            args.Add("--prerelease");
        }

        // Pass working directory for nuget.config discovery
        args.Add("--working-dir");
        args.Add(workingDirectory.FullName);

        // If explicit nuget.config is provided, use it
        if (nugetConfigFile is not null)
        {
            args.Add("--nuget-config");
            args.Add(nugetConfigFile.FullName);
        }

        // Enable verbose output for debugging - goes to stderr so won't mix with JSON on stdout
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            args.Add("--verbose");
        }

        _logger.LogDebug("Running NuGet search via aspire-managed: {Query}", query);
        _logger.LogDebug("aspire-managed path: {ManagedPath}", managedPath);
        _logger.LogDebug("NuGet search args: {Args}", string.Join(" ", args));
        _logger.LogDebug("Working directory: {WorkingDir}", workingDirectory.FullName);

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            args,
            workingDirectory: workingDirectory.FullName,
            ct: cancellationToken).ConfigureAwait(false);

        // Log stderr output (verbose info from NuGetHelper)
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogDebug("NuGetHelper stderr: {Error}", error);
        }

        if (exitCode != 0)
        {
            _logger.LogError("NuGet search failed with exit code {ExitCode}", exitCode);
            _logger.LogError("NuGet search stderr: {Error}", error);
            _logger.LogError("NuGet search stdout: {Output}", output);
            throw new NuGetPackageCacheException($"Package search failed: {error}");
        }

        _logger.LogDebug("NuGet search returned {Length} bytes", output?.Length ?? 0);

        try
        {
            if (string.IsNullOrEmpty(output))
            {
                _logger.LogWarning("NuGet search returned empty output");
                return [];
            }

            var result = JsonSerializer.Deserialize(output, BundleSearchJsonContext.Default.BundleSearchResult);
            if (result?.Packages is null)
            {
                return [];
            }

            // Convert to NuGetPackage format
            if (!exactMatch)
            {
                return result.Packages.Select(p => new NuGetPackage
                {
                    Id = p.Id,
                    Version = p.Version,
                    Source = p.Source ?? string.Empty
                }).ToList();
            }
            else
            {
                var exactMatchResultPackage = result.Packages
                    .FirstOrDefault(p => p.Id.Equals(query, StringComparison.Ordinal));
                if (exactMatchResultPackage is null || exactMatchResultPackage.AllVersions is null)
                {
                    return [];
                }
                return exactMatchResultPackage.AllVersions.Select(packageVersion => new NuGetPackage
                {
                    Id = exactMatchResultPackage.Id,
                    Version = packageVersion,
                    Source = exactMatchResultPackage.Source ?? string.Empty
                }).ToList();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse search results");
            throw new NuGetPackageCacheException($"Failed to parse search results: {ex.Message}");
        }
    }

    public async Task<IReadOnlyDictionary<string, PackageLatestVersions>> GetLatestVersionsAsync(
        IEnumerable<string> packageIds,
        DirectoryInfo workingDirectory,
        FileInfo? nugetConfigFile,
        CancellationToken cancellationToken)
    {
        var ids = packageIds
            .Where(static id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<string, PackageLatestVersions>(StringComparer.OrdinalIgnoreCase);
        }

        // Bundle mode: collapse N×2 search subprocesses into a single `aspire-managed nuget versions`
        // invocation that fans out FindPackageByIdResource calls in-process across every (id × source)
        // pair. See VersionsCommand for the implementation.
        var layout = await _bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken).ConfigureAwait(false);
        if (layout is null)
        {
            throw new InvalidOperationException("Bundle layout not found. Cannot perform NuGet versions lookup in bundle mode.");
        }

        var managedPath = layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException("aspire-managed not found in layout.");
        }

        var args = new List<string>
        {
            "nuget",
            "versions"
        };

        foreach (var id in ids)
        {
            args.Add("--id");
            args.Add(id);
        }

        args.Add("--working-dir");
        args.Add(workingDirectory.FullName);

        if (nugetConfigFile is not null)
        {
            args.Add("--nuget-config");
            args.Add(nugetConfigFile.FullName);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            args.Add("--verbose");
        }

        _logger.LogDebug("Running NuGet versions via aspire-managed for {Count} package id(s)", ids.Count);

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            args,
            workingDirectory: workingDirectory.FullName,
            ct: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogDebug("NuGetHelper stderr: {Error}", error);
        }

        if (exitCode != 0)
        {
            _logger.LogError("NuGet versions failed with exit code {ExitCode}", exitCode);
            _logger.LogError("NuGet versions stderr: {Error}", error);
            _logger.LogError("NuGet versions stdout: {Output}", output);
            throw new NuGetPackageCacheException($"Package versions lookup failed: {error}");
        }

        try
        {
            if (string.IsNullOrEmpty(output))
            {
                _logger.LogWarning("NuGet versions returned empty output");
                return new Dictionary<string, PackageLatestVersions>(StringComparer.OrdinalIgnoreCase);
            }

            var result = JsonSerializer.Deserialize(output, BundleVersionsJsonContext.Default.BundleVersionsResult);
            var dict = new Dictionary<string, PackageLatestVersions>(StringComparer.OrdinalIgnoreCase);

            if (result?.Packages is null)
            {
                return dict;
            }

            foreach (var info in result.Packages)
            {
                if (string.IsNullOrEmpty(info.Id))
                {
                    continue;
                }

                NuGetPackage? stable = info.LatestStableVersion is { Length: > 0 }
                    ? new NuGetPackage { Id = info.Id, Version = info.LatestStableVersion, Source = info.LatestStableSource ?? string.Empty }
                    : null;
                NuGetPackage? prerelease = info.LatestPrereleaseVersion is { Length: > 0 }
                    ? new NuGetPackage { Id = info.Id, Version = info.LatestPrereleaseVersion, Source = info.LatestPrereleaseSource ?? string.Empty }
                    : null;

                dict[info.Id] = new PackageLatestVersions
                {
                    LatestStable = stable,
                    LatestPrerelease = prerelease
                };
            }

            return dict;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse versions results");
            throw new NuGetPackageCacheException($"Failed to parse versions results: {ex.Message}");
        }
    }

    private IEnumerable<NuGetPackage> FilterPackages(IEnumerable<NuGetPackage> packages, Func<string, bool>? filter)
    {
        var showDeprecatedPackages = _features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false);
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

        return packages.Where(effectiveFilter);
    }

    private static bool IsOfficialOrCommunityToolkitPackage(string packageName)
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

#region JSON Models for NuGetHelper output

internal sealed class BundleSearchResult
{
    public List<BundlePackageInfo>? Packages { get; set; }
    public int TotalHits { get; set; }
}

internal sealed class BundlePackageInfo
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? Authors { get; set; }
    public List<string>? AllVersions { get; set; }
    public string? Source { get; set; }
    public bool Deprecated { get; set; }
}

[JsonSerializable(typeof(BundleSearchResult))]
[JsonSerializable(typeof(BundlePackageInfo))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BundleSearchJsonContext : JsonSerializerContext
{
}

internal sealed class BundleVersionsResult
{
    public List<BundleVersionsInfo>? Packages { get; set; }
}

internal sealed class BundleVersionsInfo
{
    public string Id { get; set; } = "";
    public string? LatestStableVersion { get; set; }
    public string? LatestStableSource { get; set; }
    public string? LatestPrereleaseVersion { get; set; }
    public string? LatestPrereleaseSource { get; set; }
}

[JsonSerializable(typeof(BundleVersionsResult))]
[JsonSerializable(typeof(BundleVersionsInfo))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BundleVersionsJsonContext : JsonSerializerContext
{
}

#endregion

