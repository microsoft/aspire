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
    private readonly ILogger<BundleNuGetPackageCache> _logger;
    private readonly IFeatures _features;

    // List of deprecated packages that should be filtered by default
    private static readonly HashSet<string> s_deprecatedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aspire.Hosting.Dapr"
    };

    public BundleNuGetPackageCache(
        IBundleService bundleService,
        ILogger<BundleNuGetPackageCache> logger,
        IFeatures features)
    {
        _bundleService = bundleService;
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
            "Aspire.ProjectTemplates",
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
        var packages = await SearchPackagesInternalAsync(
            workingDirectory,
            "Aspire.Cli",
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
            packageId,
            prerelease,
            nugetConfigFile,
            cancellationToken).ConfigureAwait(false);

        return FilterPackages(packages, filter);
    }

    private async Task<IEnumerable<NuGetPackage>> SearchPackagesInternalAsync(
        DirectoryInfo workingDirectory,
        string query,
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

        var (exitCode, output, error) = await LayoutProcessRunner.RunAsync(
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
            return result.Packages.Select(p => new NuGetPackage
            {
                Id = p.Id,
                Version = p.Version,
                Source = p.Source ?? string.Empty
            }).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse search results");
            throw new NuGetPackageCacheException($"Failed to parse search results: {ex.Message}");
        }
    }

    private IEnumerable<NuGetPackage> FilterPackages(IEnumerable<NuGetPackage> packages, Func<string, bool>? filter)
    {
        var effectiveFilter = (NuGetPackage p) =>
        {
            if (filter is not null)
            {
                return filter(p.Id);
            }

            var isOfficialPackage = IsOfficialOrCommunityToolkitPackage(p.Id);

            // Apply deprecated package filter unless the user wants to show deprecated packages
            if (isOfficialPackage && !_features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false))
            {
                return !s_deprecatedPackages.Contains(p.Id);
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

#endregion

