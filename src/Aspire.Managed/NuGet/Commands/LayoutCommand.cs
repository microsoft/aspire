// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using NuGet.ProjectModel;
using NuGet.RuntimeModel;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Layout command - creates a flat DLL layout from a project.assets.json file.
/// This enables the AppHost Server to load integration assemblies via probing paths.
/// </summary>
public static class LayoutCommand
{
    private const string RuntimeIdentifierGraphResourceName = "Aspire.Managed.RuntimeIdentifierGraph.json";
    // Match the SDK's RID expansion semantics instead of maintaining a local fallback heuristic here.
    private static readonly Lazy<RuntimeGraph> s_runtimeGraph = new(LoadRuntimeGraph);

    /// <summary>
    /// Creates the layout command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("layout", "Create flat DLL layout from project.assets.json");

        var assetsOption = new Option<string>("--assets", "-a")
        {
            Description = "Path to project.assets.json file",
            Required = true
        };
        command.Options.Add(assetsOption);

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for flat DLL layout",
            Required = true
        };
        command.Options.Add(outputOption);

        var frameworkOption = new Option<string>("--framework", "-f")
        {
            Description = "Target framework (default: net10.0)",
            DefaultValueFactory = _ => "net10.0"
        };
        command.Options.Add(frameworkOption);

        var runtimeIdentifierOption = new Option<string?>("--runtime-identifier", "--rid")
        {
            Description = "Runtime identifier used to prefer runtime-specific assets (defaults to the current runtime)"
        };
        command.Options.Add(runtimeIdentifierOption);

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output"
        };
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, ct) =>
        {
            var assetsPath = parseResult.GetValue(assetsOption)!;
            var outputPath = parseResult.GetValue(outputOption)!;
            var framework = parseResult.GetValue(frameworkOption)!;
            var runtimeIdentifier = parseResult.GetValue(runtimeIdentifierOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(ExecuteLayout(assetsPath, outputPath, framework, runtimeIdentifier, verbose));
        });

        return command;
    }

    private static int ExecuteLayout(
        string assetsPath,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        bool verbose)
    {
        if (!File.Exists(assetsPath))
        {
            Console.Error.WriteLine($"Error: Assets file not found: {assetsPath}");
            return 1;
        }

        try
        {
            // Parse the lock file
            var lockFileFormat = new LockFileFormat();
            var lockFile = lockFileFormat.Read(assetsPath);

            if (lockFile == null)
            {
                Console.Error.WriteLine("Error: Failed to parse project.assets.json");
                return 1;
            }

            var effectiveRuntimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier)
                ? RuntimeInformation.RuntimeIdentifier
                : runtimeIdentifier;
            var runtimeFallbacks = GetRuntimeFallbacks(effectiveRuntimeIdentifier);

            // Find the target for our framework and prefer the runtime-specific target when present.
            var target = lockFile.Targets
                .Where(t =>
                    t.TargetFramework.GetShortFolderName().Equals(framework, StringComparison.OrdinalIgnoreCase) ||
                    t.TargetFramework.ToString().Equals(framework, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => GetRuntimeMatchScore(t.RuntimeIdentifier, runtimeFallbacks))
                .FirstOrDefault();

            if (target == null)
            {
                Console.Error.WriteLine($"Error: Target framework '{framework}' not found in assets file");
                Console.Error.WriteLine($"Available targets: {string.Join(", ", lockFile.Targets.Select(t => t.TargetFramework.GetShortFolderName()))}");
                return 1;
            }

            // Create output directory
            Directory.CreateDirectory(outputPath);

            var copiedCount = 0;
            var skippedCount = 0;

            // Get the packages path from the lock file
            var packagesPath = lockFile.PackageFolders.FirstOrDefault()?.Path;
            if (string.IsNullOrEmpty(packagesPath))
            {
                packagesPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            }

            if (verbose)
            {
                Console.WriteLine($"Using packages path: {packagesPath}");
                Console.WriteLine($"Target framework: {target.TargetFramework.GetShortFolderName()}");
                Console.WriteLine($"Runtime identifier: {effectiveRuntimeIdentifier}");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Libraries: {0}", target.Libraries.Count));
            }

            // Process each library in the target
            foreach (var library in target.Libraries)
            {
                if (library.Type != "package")
                {
                    continue;
                }

                // Get the package folder
                var libraryName = library.Name ?? string.Empty;
                var libraryVersion = library.Version?.ToString() ?? string.Empty;
                var packagePath = Path.Combine(packagesPath, libraryName.ToLowerInvariant(), libraryVersion);

                if (!Directory.Exists(packagePath))
                {
                    if (verbose)
                    {
                        Console.WriteLine($"  Skip (not found): {libraryName}/{libraryVersion} at {packagePath}");
                    }

                    skippedCount++;
                    continue;
                }

                var runtimeTargetsByFileName = BuildBestRuntimeTargetsByFileName(library.RuntimeTargets, "runtime", runtimeFallbacks);
                var nativeRuntimeTargetsByFileName = BuildBestRuntimeTargetsByFileName(library.RuntimeTargets, "native", runtimeFallbacks);

                // Copy runtime assemblies to the output root. Prefer runtime-specific targets for the current platform
                // so probing-only assembly load contexts resolve the correct implementation.
                foreach (var runtimeAssembly in library.RuntimeAssemblies)
                {
                    // Skip placeholder files
                    if (runtimeAssembly.Path.EndsWith("_._", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourcePath = Path.Combine(packagePath, runtimeAssembly.Path.Replace('/', Path.DirectorySeparatorChar));
                    var fileName = Path.GetFileName(sourcePath);

                    if (runtimeTargetsByFileName.TryGetValue(fileName, out var runtimeTarget))
                    {
                        sourcePath = Path.Combine(packagePath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
                    }

                    // Packages that only ship runtime-specific native assets were already copied to their
                    // structured runtimes/... paths in the runtime target loop above. This loop only handles
                    // root promotion and preserving the generic native/... layout when that generic asset exists.
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var destPath = Path.Combine(outputPath, fileName);

                    // Only copy if newer or doesn't exist
                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath))
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy: {sourcePath} -> {destPath}");
                        }
                    }

                    // Also copy the XML documentation file if it exists alongside the assembly
                    var xmlSourcePath = Path.ChangeExtension(sourcePath, ".xml");
                    if (File.Exists(xmlSourcePath))
                    {
                        var xmlDestPath = Path.ChangeExtension(destPath, ".xml");
                        if (!File.Exists(xmlDestPath) ||
                            File.GetLastWriteTimeUtc(xmlSourcePath) > File.GetLastWriteTimeUtc(xmlDestPath))
                        {
                            File.Copy(xmlSourcePath, xmlDestPath, overwrite: true);
                            copiedCount++;

                            if (verbose)
                            {
                                Console.WriteLine($"  Copy (xml): {xmlSourcePath} -> {xmlDestPath}");
                            }
                        }
                    }
                }

                // Also copy runtime-specific assets preserving the runtimes/ subtree, matching dotnet build output.
                foreach (var runtimeTarget in library.RuntimeTargets)
                {
                    if (runtimeTarget.Path.EndsWith("_._", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourcePath = Path.Combine(packagePath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var destPath = Path.Combine(outputPath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(sourcePath, destPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy ({runtimeTarget.AssetType} target): {sourcePath} -> {destPath}");
                        }
                    }
                }

                foreach (var resourceAssembly in library.ResourceAssemblies)
                {
                    if (resourceAssembly.Path.EndsWith("_._", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourcePath = Path.Combine(packagePath, resourceAssembly.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var locale = resourceAssembly.Properties.TryGetValue("locale", out var value)
                        ? value
                        : Path.GetFileName(Path.GetDirectoryName(resourceAssembly.Path));

                    if (string.IsNullOrEmpty(locale))
                    {
                        continue;
                    }

                    var destPath = Path.Combine(outputPath, locale, Path.GetFileName(sourcePath));

                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(sourcePath, destPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy (resource): {sourcePath} -> {destPath}");
                        }
                    }
                }

                var handledNativeRuntimeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Also copy native libraries to the output root and preserve any runtime-specific layout.
                foreach (var nativeLib in library.NativeLibraries)
                {
                    var sourcePath = Path.Combine(packagePath, nativeLib.Path.Replace('/', Path.DirectorySeparatorChar));

                    // Early exit if source doesn't exist
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(sourcePath);
                    var rootSourcePath = sourcePath;

                    if (nativeRuntimeTargetsByFileName.TryGetValue(fileName, out var nativeRuntimeTarget))
                    {
                        var runtimeTargetPath = Path.Combine(packagePath, nativeRuntimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(runtimeTargetPath))
                        {
                            rootSourcePath = runtimeTargetPath;
                            handledNativeRuntimeTargets.Add(fileName);
                        }
                    }

                    var destPath = Path.Combine(outputPath, fileName);

                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(rootSourcePath) > File.GetLastWriteTimeUtc(destPath))
                    {
                        File.Copy(rootSourcePath, destPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy (native): {rootSourcePath} -> {destPath}");
                        }
                    }

                    var structuredDestPath = Path.Combine(outputPath, nativeLib.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(structuredDestPath) ||
                        File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(structuredDestPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(structuredDestPath)!);
                        File.Copy(sourcePath, structuredDestPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy (native path): {sourcePath} -> {structuredDestPath}");
                        }
                    }
                }

                foreach (var nativeRuntimeTarget in nativeRuntimeTargetsByFileName.Values)
                {
                    var sourcePath = Path.Combine(packagePath, nativeRuntimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(sourcePath);
                    if (handledNativeRuntimeTargets.Contains(fileName))
                    {
                        continue;
                    }

                    var destPath = Path.Combine(outputPath, fileName);
                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath))
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy (native target): {sourcePath} -> {destPath}");
                        }
                    }
                }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Layout created: {0} files copied to {1}", copiedCount, outputPath));
            if (skippedCount > 0 && verbose)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  ({0} packages skipped - not found in cache)", skippedCount));
            }

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

    private static List<string> GetRuntimeFallbacks(string? runtimeIdentifier)
    {
        if (string.IsNullOrEmpty(runtimeIdentifier))
        {
            return [];
        }

        return s_runtimeGraph.Value
            .ExpandRuntime(runtimeIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetRuntimeMatchScore(string? runtimeIdentifier, IReadOnlyList<string> runtimeFallbacks)
    {
        if (string.IsNullOrEmpty(runtimeIdentifier))
        {
            return runtimeFallbacks.Count;
        }

        for (var i = 0; i < runtimeFallbacks.Count; i++)
        {
            if (string.Equals(runtimeIdentifier, runtimeFallbacks[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static Dictionary<string, LockFileRuntimeTarget> BuildBestRuntimeTargetsByFileName(
        IEnumerable<LockFileRuntimeTarget> runtimeTargets,
        string assetType,
        IReadOnlyList<string> runtimeFallbacks)
    {
        return runtimeTargets
            .Where(runtimeTarget =>
                string.Equals(runtimeTarget.AssetType, assetType, StringComparison.OrdinalIgnoreCase) &&
                !runtimeTarget.Path.EndsWith("_._", StringComparison.OrdinalIgnoreCase))
            .Select(runtimeTarget => new
            {
                RuntimeTarget = runtimeTarget,
                Score = GetRuntimeMatchScore(runtimeTarget.Runtime, runtimeFallbacks)
            })
            .Where(runtimeTarget => runtimeTarget.Score != int.MaxValue)
            .GroupBy(runtimeTarget => Path.GetFileName(runtimeTarget.RuntimeTarget.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(runtimeTarget => runtimeTarget.Score).First().RuntimeTarget,
                StringComparer.OrdinalIgnoreCase);
    }

    private static RuntimeGraph LoadRuntimeGraph()
    {
        using var stream = typeof(LayoutCommand).Assembly.GetManifestResourceStream(RuntimeIdentifierGraphResourceName)
            ?? throw new InvalidOperationException($"Embedded runtime identifier graph '{RuntimeIdentifierGraphResourceName}' was not found.");

        return JsonRuntimeFormat.ReadRuntimeGraph(stream);
    }
}
