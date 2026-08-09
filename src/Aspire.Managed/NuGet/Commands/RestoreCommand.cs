// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.CommandLine;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Restore command - restores NuGet packages without requiring a .csproj file.
/// Uses NuGet's RestoreRunner to produce a project.assets.json file.
/// </summary>
public static class RestoreCommand
{
    private const string RuntimeIdentifierGraphFileName = "RuntimeIdentifierGraph.json";
    private const string RuntimeIdentifierGraphResourceName = "Aspire.Managed.RuntimeIdentifierGraph.json";

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

        var runtimeIdentifierOption = new Option<string?>("--runtime-identifier", "--rid")
        {
            Description = "Runtime identifier to restore for"
        };
        command.Options.Add(runtimeIdentifierOption);

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for project.assets.json",
            DefaultValueFactory = _ => "./obj"
        };
        command.Options.Add(outputOption);

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
            var runtimeIdentifier = parseResult.GetValue(runtimeIdentifierOption);
            var output = parseResult.GetValue(outputOption)!;
            var sources = parseResult.GetValue(sourceOption) ?? [];
            var nugetConfigPath = parseResult.GetValue(configOption);
            var workingDir = parseResult.GetValue(workingDirOption);
            var noNugetOrg = parseResult.GetValue(noNugetOrgOption);
            var verbose = parseResult.GetValue(verboseOption);

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

            return await ExecuteRestoreAsync(packages, framework, runtimeIdentifier, output, sources, nugetConfigPath, workingDir, noNugetOrg, verbose).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> ExecuteRestoreAsync(
        List<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string output,
        string[] cliSources,
        string? nugetConfigPath,
        string? workingDir,
        bool noNugetOrg,
        bool verbose)
    {
        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(outputPath);

        var logger = new NuGetLogger(verbose);

        try
        {
            // Load NuGet settings once — handles working dir, config file, and machine-wide settings.
            var machineWideSettings = new XPlatMachineWideSetting();
            var settings = Settings.LoadDefaultSettings(workingDir, nugetConfigPath, machineWideSettings);

            if (verbose)
            {
                Console.WriteLine($"Restoring {packages.Count} packages for {framework}");
                Console.WriteLine($"Output: {outputPath}");
                if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
                {
                    Console.WriteLine($"Runtime identifier: {runtimeIdentifier}");
                }
                if (workingDir is not null)
                {
                    Console.WriteLine($"Working dir: {workingDir}");
                }
                if (nugetConfigPath is not null)
                {
                    Console.WriteLine($"NuGet config: {nugetConfigPath}");
                }
            }

            // Resolve package sources using NuGet's PackageSourceProvider
            var packageSources = ResolvePackageSources(settings, cliSources, noNugetOrg);

            var nugetFramework = NuGetFramework.Parse(framework);

            // Build PackageSpec and DependencyGraphSpec
            var packageSpec = BuildPackageSpec(packages, nugetFramework, runtimeIdentifier, outputPath, packageSources, settings);

            var dgSpec = new DependencyGraphSpec();
            dgSpec.AddProject(packageSpec);
            dgSpec.AddRestore(packageSpec.RestoreMetadata.ProjectUniqueName);

            // Pass settings to the provider so it reuses our pre-loaded settings
            var providerCache = new RestoreCommandProvidersCache();
            var dgProvider = new DependencyGraphSpecRequestProvider(providerCache, dgSpec, settings);

            // Run restore — let NuGet handle source credentials, parallel execution, etc.
            using var cacheContext = new SourceCacheContext();
            var restoreArgs = new RestoreArgs
            {
                CacheContext = cacheContext,
                Log = logger,
                PreLoadedRequestProviders = [dgProvider],
                DisableParallel = Environment.ProcessorCount == 1,
                AllowNoOp = false,
                MachineWideSettings = machineWideSettings,
            };

            // Initialize NuGet's trust store with embedded trusted root certificates
            // so that package signature verification works on Linux.
            TrustedRootsHelper.InitializeTrustStore();

            var results = await RestoreRunner.RunAsync(restoreArgs).ConfigureAwait(false);
            var summary = results.Count > 0 ? results[0] : null;

            if (summary is null)
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

    private static List<PackageSource> ResolvePackageSources(ISettings settings, string[] cliSources, bool noNugetOrg)
    {
        // Load enabled sources from NuGet config
        var provider = new PackageSourceProvider(settings);
        var sources = provider.LoadPackageSources().Where(s => s.IsEnabled).ToList();

        // Append CLI --source values (matching NuGet's behavior of merging, not replacing)
        foreach (var cliSource in cliSources)
        {
            if (!sources.Any(s => s.Source.Equals(cliSource, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new PackageSource(cliSource));
            }
        }

        // Add nuget.org as a fallback source unless opted out
        if (!noNugetOrg)
        {
            const string nugetOrgUrl = "https://api.nuget.org/v3/index.json";
            if (!sources.Any(s => s.Source.Equals(nugetOrgUrl, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Note: Adding nuget.org as fallback package source. Use --no-nuget-org to disable.");
                sources.Add(new PackageSource(nugetOrgUrl, "nuget.org"));
            }
        }

        return sources;
    }

    private static PackageSpec BuildPackageSpec(
        List<(string Id, string Version)> packages,
        NuGetFramework framework,
        string? runtimeIdentifier,
        string outputPath,
        List<PackageSource> sources,
        ISettings settings)
    {
        var projectName = "AspireRestore";
        var projectPath = Path.Combine(outputPath, "project.json");
        var tfmShort = framework.GetShortFolderName();
        var runtimeIdentifierGraphPath = !string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? EnsureRuntimeIdentifierGraphPath(outputPath)
            : null;

        var dependencies = packages.Select(p => new LibraryDependency
        {
            LibraryRange = new LibraryRange(
                p.Id,
                VersionRange.Parse(p.Version),
                LibraryDependencyTarget.Package)
        }).ToImmutableArray();

        var tfInfo = new TargetFrameworkInformation
        {
            FrameworkName = framework,
            TargetAlias = tfmShort,
            Dependencies = dependencies,
            RuntimeIdentifierGraphPath = runtimeIdentifierGraphPath
        };

        var restoreMetadata = new ProjectRestoreMetadata
        {
            ProjectUniqueName = projectName,
            ProjectName = projectName,
            ProjectPath = projectPath,
            ProjectStyle = ProjectStyle.PackageReference,
            OutputPath = outputPath,
            PackagesPath = SettingsUtility.GetGlobalPackagesFolder(settings),
            OriginalTargetFrameworks = [tfmShort],
            ConfigFilePaths = settings.GetConfigFilePaths().ToList(),
        };

        foreach (var source in sources)
        {
            restoreMetadata.Sources.Add(source);
        }

        restoreMetadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(framework)
        {
            TargetAlias = tfmShort
        });

        var packageSpec = new PackageSpec([tfInfo])
        {
            Name = projectName,
            FilePath = projectPath,
            RestoreMetadata = restoreMetadata,
        };

        if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            packageSpec.RuntimeGraph = new RuntimeGraph([new RuntimeDescription(runtimeIdentifier)]);
        }

        return packageSpec;
    }

    private static string EnsureRuntimeIdentifierGraphPath(string outputPath)
    {
        var graphPath = Path.Combine(outputPath, RuntimeIdentifierGraphFileName);
        if (File.Exists(graphPath))
        {
            return graphPath;
        }

        using var resourceStream = typeof(RestoreCommand).Assembly.GetManifestResourceStream(RuntimeIdentifierGraphResourceName)
            ?? throw new InvalidOperationException($"Embedded runtime identifier graph resource '{RuntimeIdentifierGraphResourceName}' was not found.");
        using var fileStream = File.Create(graphPath);
        resourceStream.CopyTo(fileStream);

        return graphPath;
    }
}
