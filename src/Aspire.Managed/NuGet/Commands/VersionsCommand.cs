// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Versions command - resolves the latest stable and prerelease version for a set of package ids
/// using NuGet's <see cref="FindPackageByIdResource"/> across all configured sources in parallel.
/// Used by `aspire update` to collapse N per-package "search" subprocess invocations into a single
/// batched call. Unlike a search-relevance query, FindPackageByIdResource asks each feed for the
/// exact id, so the result does not depend on search ranking or take limits.
/// </summary>
public static class VersionsCommand
{
    /// <summary>
    /// Creates the versions command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("versions", "Resolve the latest stable and prerelease versions for a set of package ids");

        var idOption = new Option<string[]>("--id", "-i")
        {
            Description = "Package id to resolve (can be specified multiple times)",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        command.Options.Add(idOption);

        var sourceOption = new Option<string[]>("--source")
        {
            Description = "NuGet feed URL (can specify multiple)",
            DefaultValueFactory = _ => Array.Empty<string>(),
            AllowMultipleArgumentsPerToken = true
        };
        command.Options.Add(sourceOption);

        var configOption = new Option<string?>("--nuget-config")
        {
            Description = "Path to nuget.config file"
        };
        command.Options.Add(configOption);

        var workingDirOption = new Option<string?>("--working-dir", "-d")
        {
            Description = "Working directory to search for nuget.config"
        };
        command.Options.Add(workingDirOption);

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output"
        };
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var ids = parseResult.GetValue(idOption) ?? [];
            var sources = parseResult.GetValue(sourceOption) ?? [];
            var configPath = parseResult.GetValue(configOption);
            var workingDir = parseResult.GetValue(workingDirOption);
            var verbose = parseResult.GetValue(verboseOption);

            return await ExecuteAsync(ids, sources, configPath, workingDir, verbose, ct).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string[] ids,
        string[] explicitSources,
        string? configPath,
        string? workingDir,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var logger = new NuGetLogger(verbose);

        try
        {
            var settings = LoadSettings(configPath, workingDir);
            var packageSources = LoadPackageSources(settings, explicitSources, verbose);

            // Cache the SourceRepository + FindPackageByIdResource per source to avoid re-resolving
            // service indexes for every id when many ids are requested in one invocation.
            var resourcesBySource = new Dictionary<PackageSource, FindPackageByIdResource?>();
            foreach (var source in packageSources)
            {
                try
                {
                    var repository = Repository.Factory.GetCoreV3(source);
                    var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
                    resourcesBySource[source] = resource;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to initialize source {source.Name}: {ex.Message}");
                    resourcesBySource[source] = null;
                }
            }

            using var cacheContext = new SourceCacheContext();

            // Resolve every id × source pair in parallel. Each query is a single HTTP GET against
            // the feed's PackageBaseAddress (flat container) for V3 sources.
            var results = await Task.WhenAll(ids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => ResolveIdAsync(id, resourcesBySource, cacheContext, logger, verbose, cancellationToken)))
                .ConfigureAwait(false);

            var output = new VersionsResult
            {
                Packages = results.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList()
            };

            Console.WriteLine(JsonSerializer.Serialize(output, VersionsJsonContext.Default.VersionsResult));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 1;
        }
    }

    private static async Task<PackageVersionsInfo> ResolveIdAsync(
        string id,
        Dictionary<PackageSource, FindPackageByIdResource?> resourcesBySource,
        SourceCacheContext cacheContext,
        INuGetLogger logger,
        bool verbose,
        CancellationToken cancellationToken)
    {
        // Issue parallel GetAllVersionsAsync calls per source for this id; collapse into one
        // (latestStable, latestPrerelease, source) tuple. We pick the source that returned the
        // overall winning version so the caller knows where each package came from.
        var perSource = await Task.WhenAll(resourcesBySource.Select(async kvp =>
        {
            var (source, resource) = kvp;
            if (resource is null)
            {
                return (Source: source, Versions: (IEnumerable<NuGetVersion>)Array.Empty<NuGetVersion>());
            }

            try
            {
                var versions = await resource.GetAllVersionsAsync(id, cacheContext, logger, cancellationToken).ConfigureAwait(false);
                return (Source: source, Versions: versions ?? Array.Empty<NuGetVersion>());
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture, "Warning: Failed to get versions for '{0}' from {1}: {2}", id, source.Name, ex.Message));
                }
                return (Source: source, Versions: (IEnumerable<NuGetVersion>)Array.Empty<NuGetVersion>());
            }
        })).ConfigureAwait(false);

        NuGetVersion? bestStable = null;
        PackageSource? bestStableSource = null;
        NuGetVersion? bestPrerelease = null;
        PackageSource? bestPrereleaseSource = null;

        foreach (var (source, versions) in perSource)
        {
            foreach (var version in versions)
            {
                if (version.IsPrerelease)
                {
                    if (bestPrerelease is null || version > bestPrerelease)
                    {
                        bestPrerelease = version;
                        bestPrereleaseSource = source;
                    }
                }
                else
                {
                    if (bestStable is null || version > bestStable)
                    {
                        bestStable = version;
                        bestStableSource = source;
                    }
                }
            }
        }

        return new PackageVersionsInfo
        {
            Id = id,
            LatestStableVersion = bestStable?.OriginalVersion ?? bestStable?.ToNormalizedString(),
            LatestStableSource = bestStableSource?.Source,
            LatestPrereleaseVersion = bestPrerelease?.OriginalVersion ?? bestPrerelease?.ToNormalizedString(),
            LatestPrereleaseSource = bestPrereleaseSource?.Source
        };
    }

    private static ISettings LoadSettings(string? configPath, string? workingDir)
    {
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            var configDir = Path.GetDirectoryName(configPath)!;
            var configFile = Path.GetFileName(configPath);
            return Settings.LoadSpecificSettings(configDir, configFile);
        }

        var searchDir = workingDir ?? Directory.GetCurrentDirectory();
        return Settings.LoadDefaultSettings(searchDir);
    }

    private static List<PackageSource> LoadPackageSources(ISettings settings, string[] explicitSources, bool verbose)
    {
        var packageSources = new List<PackageSource>();

        foreach (var source in explicitSources)
        {
            packageSources.Add(new PackageSource(source));
            if (verbose)
            {
                Console.Error.WriteLine($"Using explicit source: {source}");
            }
        }

        if (packageSources.Count == 0)
        {
            var provider = new PackageSourceProvider(settings);
            foreach (var source in provider.LoadPackageSources())
            {
                if (source.IsEnabled)
                {
                    packageSources.Add(source);
                    if (verbose)
                    {
                        Console.Error.WriteLine($"Using source from config: {source.Name} ({source.Source})");
                    }
                }
            }
        }

        if (packageSources.Count == 0)
        {
            // Match SearchCommand behavior: fall back to nuget.org if no sources are configured at all.
            var defaultSource = new PackageSource("https://api.nuget.org/v3/index.json", "nuget.org");
            packageSources.Add(defaultSource);
            Console.Error.WriteLine("Note: No package sources configured, using nuget.org as fallback.");
        }

        return packageSources;
    }
}

#region JSON Models

/// <summary>
/// Result of a versions lookup.
/// </summary>
public sealed class VersionsResult
{
    /// <summary>Gets or sets the list of resolved package versions.</summary>
    public List<PackageVersionsInfo> Packages { get; set; } = [];
}

/// <summary>
/// Latest known stable + prerelease versions for a single package id.
/// Either field may be null when no matching version exists on any configured source.
/// </summary>
public sealed class PackageVersionsInfo
{
    /// <summary>Gets or sets the package id.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the latest stable version observed across sources.</summary>
    public string? LatestStableVersion { get; set; }
    /// <summary>Gets or sets the source URL that returned the latest stable version.</summary>
    public string? LatestStableSource { get; set; }
    /// <summary>Gets or sets the latest prerelease version observed across sources.</summary>
    public string? LatestPrereleaseVersion { get; set; }
    /// <summary>Gets or sets the source URL that returned the latest prerelease version.</summary>
    public string? LatestPrereleaseSource { get; set; }
}

[JsonSerializable(typeof(VersionsResult))]
[JsonSerializable(typeof(PackageVersionsInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class VersionsJsonContext : JsonSerializerContext
{
}

#endregion
