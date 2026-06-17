// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using INuGetLogger = NuGet.Common.ILogger;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Search command - searches NuGet feeds for packages using NuGet.Protocol.
/// </summary>
public static class SearchCommand
{
    /// <summary>
    /// Creates the search command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("search", "Search for NuGet packages");

        var queryOption = new Option<string>("--query", "-q")
        {
            Description = "Search query string",
            Required = true
        };
        command.Options.Add(queryOption);

        var prereleaseOption = new Option<bool>("--prerelease", "-p")
        {
            Description = "Include prerelease packages"
        };
        command.Options.Add(prereleaseOption);

        var takeOption = new Option<int>("--take", "-t")
        {
            Description = "Maximum number of results (default: 100)",
            DefaultValueFactory = _ => 100
        };
        command.Options.Add(takeOption);

        var skipOption = new Option<int>("--skip", "-s")
        {
            Description = "Number of results to skip (default: 0)",
            DefaultValueFactory = _ => 0
        };
        command.Options.Add(skipOption);

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

        var formatOption = new Option<string>("--format", "-f")
        {
            Description = "Output format: json or text (default: json)",
            DefaultValueFactory = _ => "json"
        };
        command.Options.Add(formatOption);

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output"
        };
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var query = parseResult.GetValue(queryOption)!;
            var prerelease = parseResult.GetValue(prereleaseOption);
            var take = parseResult.GetValue(takeOption);
            var skip = parseResult.GetValue(skipOption);
            var sources = parseResult.GetValue(sourceOption) ?? [];
            var configPath = parseResult.GetValue(configOption);
            var workingDir = parseResult.GetValue(workingDirOption);
            var format = parseResult.GetValue(formatOption) ?? "json";
            var verbose = parseResult.GetValue(verboseOption);

            return await ExecuteSearchAsync(query, prerelease, take, skip, sources, configPath, workingDir, format, verbose).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> ExecuteSearchAsync(
        string query,
        bool prerelease,
        int take,
        int skip,
        string[] explicitSources,
        string? configPath,
        string? workingDir,
        string format,
        bool verbose)
    {
        var logger = new NuGetLogger(verbose);
        var allPackages = new List<PackageInfo>();

        try
        {
            // Load settings from nuget.config
            var settings = LoadSettings(configPath, workingDir);
            var packageSources = LoadPackageSources(settings, explicitSources, verbose);

            if (verbose)
            {
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture, "Searching {0} source(s) for '{1}'", packageSources.Count, query));
            }

            // Search each source in parallel using NuGet.Protocol
            var searchFilter = new SearchFilter(prerelease);
            var searchTasks = packageSources.Select(async source =>
            {
                try
                {
                    if (verbose)
                    {
                        Console.Error.WriteLine($"Searching {source.Name} ({source.Source})...");
                    }

                    return await SearchSourceAsync(source, query, searchFilter, skip, take, logger).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to search {source.Name}: {ex.Message}");
                    return new List<PackageInfo>();
                }
            }).ToList();

            var searchResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);
            foreach (var packages in searchResults)
            {
                allPackages.AddRange(packages);
            }

            // Deduplicate by package ID, keeping the highest version
            var dedupedPackages = allPackages
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(p => p.Version).First())
                .OrderBy(p => p.Id)
                .Take(take)
                .ToList();

            OutputResults(dedupedPackages, format);
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

        // Add explicit sources first
        foreach (var source in explicitSources)
        {
            packageSources.Add(new PackageSource(source));
            if (verbose)
            {
                Console.Error.WriteLine($"Using explicit source: {source}");
            }
        }

        // If no explicit sources, load from settings
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

        // Default to nuget.org if still no sources
        if (packageSources.Count == 0)
        {
            var defaultSource = new PackageSource("https://api.nuget.org/v3/index.json", "nuget.org");
            packageSources.Add(defaultSource);
            Console.Error.WriteLine("Note: No package sources configured, using nuget.org as fallback.");
        }

        return packageSources;
    }

    private static async Task<List<PackageInfo>> SearchSourceAsync(
        PackageSource source,
        string query,
        SearchFilter filter,
        int skip,
        int take,
        INuGetLogger logger)
    {
        var repository = Repository.Factory.GetCoreV3(source);
        var searchResource = await repository.GetResourceAsync<PackageSearchResource>().ConfigureAwait(false);

        if (searchResource is null)
        {
            return [];
        }

        var results = await searchResource.SearchAsync(
            query,
            filter,
            skip,
            take,
            logger,
            CancellationToken.None).ConfigureAwait(false);

        var packages = new List<PackageInfo>();
        foreach (var result in results)
        {
            var versions = await result.GetVersionsAsync().ConfigureAwait(false);
            var allVersions = versions?.Select(v => v.Version.ToString()).ToList() ?? [];

            packages.Add(new PackageInfo
            {
                Id = result.Identity.Id,
                Version = result.Identity.Version.ToString(),
                Description = result.Description,
                Authors = result.Authors,
                AllVersions = allVersions,
                Source = source.Source,
                Deprecated = await result.GetDeprecationMetadataAsync().ConfigureAwait(false) is not null
            });
        }

        return packages;
    }

    private static void OutputResults(List<PackageInfo> packages, string format)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var result = new SearchResult
            {
                Packages = packages,
                TotalHits = packages.Count
            };
            Console.WriteLine(JsonSerializer.Serialize(result, SearchJsonContext.Default.SearchResult));
        }
        else
        {
            foreach (var pkg in packages)
            {
                Console.WriteLine($"{pkg.Id} {pkg.Version}");
                if (!string.IsNullOrEmpty(pkg.Description))
                {
                    Console.WriteLine($"  {pkg.Description}");
                }

                Console.WriteLine();
            }
        }
    }
}

#region JSON Models

/// <summary>
/// Result of a package search.
/// </summary>
public sealed class SearchResult
{
    /// <summary>Gets or sets the list of packages found.</summary>
    public List<PackageInfo> Packages { get; set; } = [];
    /// <summary>Gets or sets the total number of hits.</summary>
    public int TotalHits { get; set; }
}

/// <summary>
/// Information about a NuGet package.
/// </summary>
public sealed class PackageInfo
{
    /// <summary>Gets or sets the package ID.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the latest version.</summary>
    public string Version { get; set; } = "";
    /// <summary>Gets or sets the package description.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the package authors.</summary>
    public string? Authors { get; set; }
    /// <summary>Gets or sets all available versions.</summary>
    public List<string> AllVersions { get; set; } = [];
    /// <summary>Gets or sets the source feed.</summary>
    public string? Source { get; set; }
    /// <summary>Gets or sets whether the package is deprecated.</summary>
    public bool Deprecated { get; set; }
}

[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(PackageInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SearchJsonContext : JsonSerializerContext
{
}

#endregion
