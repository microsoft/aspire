// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Restore command - restores NuGet packages without requiring a .csproj file.
/// Uses NuGet's RestoreRunner to produce a project.assets.json file.
/// </summary>
public static class RestoreCommand
{
    /// <summary>
    /// Creates the restore command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("restore", "Restore NuGet packages");

        var packageOption = new Option<string[]>("--package", "-p")
        {
            Description = "Package reference as 'PackageId,Version' (can specify multiple)",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        command.Options.Add(packageOption);

        var frameworkOption = new Option<string>("--framework", "-f")
        {
            Description = "Target framework (default: net10.0)",
            DefaultValueFactory = _ => "net10.0"
        };
        command.Options.Add(frameworkOption);

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for project.assets.json",
            DefaultValueFactory = _ => "./obj"
        };
        command.Options.Add(outputOption);

        var packagesDirOption = new Option<string?>("--packages-dir")
        {
            Description = "NuGet packages directory"
        };
        command.Options.Add(packagesDirOption);

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

        var workingDirOption = new Option<string?>("--working-dir", "-w")
        {
            Description = "Working directory for nuget.config discovery"
        };
        command.Options.Add(workingDirOption);

        var noNugetOrgOption = new Option<bool>("--no-nuget-org")
        {
            Description = "Don't add nuget.org as fallback source"
        };
        command.Options.Add(noNugetOrgOption);

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable verbose output"
        };
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, ct) =>
        {
            // Note: ?? is used for null-safety even with DefaultValueFactory because GetValue returns T?
            var packageArgs = parseResult.GetValue(packageOption) ?? [];
            var framework = parseResult.GetValue(frameworkOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var packagesDir = parseResult.GetValue(packagesDirOption);
            var sources = parseResult.GetValue(sourceOption) ?? [];
            var nugetConfigPath = parseResult.GetValue(configOption);
            var workingDir = parseResult.GetValue(workingDirOption);
            var noNugetOrg = parseResult.GetValue(noNugetOrgOption);
            var verbose = parseResult.GetValue(verboseOption);

            // Validate that both nuget-config and sources aren't provided together
            if (!string.IsNullOrEmpty(nugetConfigPath) && sources.Length > 0)
            {
                Console.Error.WriteLine("Error: Cannot specify both --nuget-config and --source. Use one or the other.");
                return 1;
            }

            // Parse packages (format: PackageId,Version)
            var packages = new List<(string Id, string Version)>();
            foreach (var pkgArg in packageArgs)
            {
                if (verbose)
                {
                    Console.WriteLine($"Parsing package argument: {pkgArg}");
                }
                var parts = pkgArg.Split(',', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    Console.Error.WriteLine($"Error: Package argument '{pkgArg}' must be in format 'PackageId,Version'");
                    return 1;
                }
                packages.Add((parts[0], parts[1]));
            }

            return await ExecuteRestoreAsync([.. packages], framework, output, packagesDir, sources, nugetConfigPath, workingDir, noNugetOrg, verbose).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> ExecuteRestoreAsync(
        (string Id, string Version)[] packages,
        string framework,
        string output,
        string? packagesDir,
        string[] sources,
        string? nugetConfigPath,
        string? workingDir,
        bool noNugetOrg,
        bool verbose)
    {
        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(outputPath);

        packagesDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");

        var logger = new NuGetLogger(verbose);

        try
        {
            var nugetFramework = NuGetFramework.Parse(framework);

            if (verbose)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Restoring {0} packages for {1}", packages.Length, framework));
                Console.WriteLine($"Output: {outputPath}");
                Console.WriteLine($"Packages: {packagesDir}");
                if (workingDir is not null)
                {
                    Console.WriteLine($"Working dir: {workingDir}");
                }
                if (nugetConfigPath is not null)
                {
                    Console.WriteLine($"NuGet config: {nugetConfigPath}");
                }
            }

            // Load package sources
            var packageSources = LoadPackageSources(sources, nugetConfigPath, workingDir, noNugetOrg, verbose);

            // Build PackageSpec
            var packageSpec = BuildPackageSpec(packages, nugetFramework, outputPath, packagesDir, packageSources);

            // Create DependencyGraphSpec
            var dgSpec = new DependencyGraphSpec();
            dgSpec.AddProject(packageSpec);
            dgSpec.AddRestore(packageSpec.RestoreMetadata.ProjectUniqueName);

            // Setup providers
            var providerCache = new RestoreCommandProvidersCache();
            var providers = new List<IPreLoadedRestoreRequestProvider>
            {
                new DependencyGraphSpecRequestProvider(providerCache, dgSpec)
            };

            // Run restore
            using var cacheContext = new SourceCacheContext();
            var restoreContext = new RestoreArgs
            {
                CacheContext = cacheContext,
                Log = logger,
                PreLoadedRequestProviders = providers,
                DisableParallel = Environment.ProcessorCount == 1,
                AllowNoOp = false,
                GlobalPackagesFolder = packagesDir
            };

            if (verbose)
            {
                Console.WriteLine("Running restore...");
            }

            var results = await RestoreRunner.RunAsync(restoreContext).ConfigureAwait(false);
            var summary = results.Count > 0 ? results[0] : null;

            if (summary == null)
            {
                Console.Error.WriteLine("Error: Restore returned no results");
                return 1;
            }

            if (!summary.Success)
            {
                var errors = string.Join(Environment.NewLine,
                    summary.Errors?.Select(e => e.Message) ?? ["Unknown error"]);
                Console.Error.WriteLine($"Error: Restore failed: {errors}");
                return 1;
            }

            var assetsPath = Path.Combine(outputPath, "project.assets.json");
            Console.WriteLine($"Restore completed successfully");
            Console.WriteLine($"Assets file: {assetsPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine(ex.ToString());
            }

            return 1;
        }
    }

    private static List<PackageSource> LoadPackageSources(string[] sources, string? nugetConfigPath, string? workingDir, bool noNugetOrg, bool verbose)
    {
        var packageSources = new List<PackageSource>();

        // Add explicit sources first (they get priority)
        foreach (var source in sources)
        {
            packageSources.Add(new PackageSource(source));
        }

        // Load from specific config file if specified
        if (!string.IsNullOrEmpty(nugetConfigPath) && File.Exists(nugetConfigPath))
        {
            var configDir = Path.GetDirectoryName(nugetConfigPath)!;
            var configFile = Path.GetFileName(nugetConfigPath);
            var settings = Settings.LoadSpecificSettings(configDir, configFile);
            var provider = new PackageSourceProvider(settings);

            foreach (var source in provider.LoadPackageSources())
            {
                if (source.IsEnabled && !packageSources.Any(s => s.Source == source.Source))
                {
                    packageSources.Add(source);
                }
            }
        }
        // Auto-discover nuget.config from working directory if specified
        else if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
        {
            try
            {
                // LoadDefaultSettings walks up the directory tree looking for nuget.config files
                var settings = Settings.LoadDefaultSettings(workingDir);
                var provider = new PackageSourceProvider(settings);

                if (verbose)
                {
                    // Show the config file paths that were loaded
                    var configPaths = settings.GetConfigFilePaths();
                    Console.WriteLine($"Discovering NuGet config from: {workingDir}");
                    foreach (var configPath in configPaths)
                    {
                        Console.WriteLine($"  Loaded config: {configPath}");
                    }
                }

                foreach (var source in provider.LoadPackageSources())
                {
                    if (source.IsEnabled && !packageSources.Any(s => s.Source == source.Source))
                    {
                        if (verbose)
                        {
                            Console.WriteLine($"  Discovered source: {source.Name ?? source.Source}");
                        }
                        packageSources.Add(source);
                    }
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.WriteLine($"Warning: Failed to load NuGet config from {workingDir}: {ex.ToString()}");
                }
            }
        }

        // Add nuget.org as a fallback source unless opted out
        if (!noNugetOrg)
        {
            const string nugetOrgUrl = "https://api.nuget.org/v3/index.json";
            if (!packageSources.Any(s => s.Source.Equals(nugetOrgUrl, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Note: Adding nuget.org as fallback package source. Use --no-nuget-org to disable.");
                packageSources.Add(new PackageSource(nugetOrgUrl, "nuget.org"));
            }
        }

        if (verbose)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Using {0} package sources:", packageSources.Count));
            foreach (var source in packageSources)
            {
                Console.WriteLine($"  - {source.Name ?? source.Source}");
            }
        }

        return packageSources;
    }

    private static PackageSpec BuildPackageSpec(
        (string Id, string Version)[] packages,
        NuGetFramework framework,
        string outputPath,
        string packagesPath,
        List<PackageSource> sources)
    {
        var projectName = "AspireRestore";
        var projectPath = Path.Combine(outputPath, "project.json");
        var tfmShort = framework.GetShortFolderName();

        // Build dependencies
        var dependencies = packages.Select(p => new LibraryDependency
        {
            LibraryRange = new LibraryRange(
                p.Id,
                VersionRange.Parse(p.Version),
                LibraryDependencyTarget.Package)
        }).ToImmutableArray();

        // Build target framework info
        var tfInfo = new TargetFrameworkInformation
        {
            FrameworkName = framework,
            TargetAlias = tfmShort,
            Dependencies = dependencies
        };

        // Build restore metadata
        var restoreMetadata = new ProjectRestoreMetadata
        {
            ProjectUniqueName = projectName,
            ProjectName = projectName,
            ProjectPath = projectPath,
            ProjectStyle = ProjectStyle.PackageReference,
            OutputPath = outputPath,
            PackagesPath = packagesPath,
            OriginalTargetFrameworks = [tfmShort],
        };

        // Add sources
        foreach (var source in sources)
        {
            restoreMetadata.Sources.Add(source);
        }

        // Add target framework
        restoreMetadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(framework)
        {
            TargetAlias = tfmShort
        });

        return new PackageSpec([tfInfo])
        {
            Name = projectName,
            FilePath = projectPath,
            RestoreMetadata = restoreMetadata,
        };
    }
}
