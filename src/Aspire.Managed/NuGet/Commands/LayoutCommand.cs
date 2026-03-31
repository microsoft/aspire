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

            var target = ResolveTarget(lockFile, framework, runtimeFallbacks);

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

            var packagesPath = GetPackagesPath(lockFile);

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
                var (libraryCopiedCount, librarySkippedCount) = ProcessLibrary(
                    library,
                    packagesPath,
                    outputPath,
                    runtimeFallbacks,
                    verbose);

                copiedCount += libraryCopiedCount;
                skippedCount += librarySkippedCount;
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

    private static LockFileTarget? ResolveTarget(LockFile lockFile, string framework, IReadOnlyList<string> runtimeFallbacks)
    {
        return lockFile.Targets
            .Where(t =>
                t.TargetFramework.GetShortFolderName().Equals(framework, StringComparison.OrdinalIgnoreCase) ||
                t.TargetFramework.ToString().Equals(framework, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => GetRuntimeMatchScore(t.RuntimeIdentifier, runtimeFallbacks))
            .FirstOrDefault();
    }

    private static string GetPackagesPath(LockFile lockFile)
    {
        var packagesPath = lockFile.PackageFolders.FirstOrDefault()?.Path;
        if (!string.IsNullOrEmpty(packagesPath))
        {
            return packagesPath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");
    }

    private static (int CopiedCount, int SkippedCount) ProcessLibrary(
        LockFileTargetLibrary library,
        string packagesPath,
        string outputPath,
        IReadOnlyList<string> runtimeFallbacks,
        bool verbose)
    {
        if (library.Type != "package")
        {
            return (0, 0);
        }

        var libraryName = library.Name ?? string.Empty;
        var libraryVersion = library.Version?.ToString() ?? string.Empty;
        var packagePath = Path.Combine(packagesPath, libraryName.ToLowerInvariant(), libraryVersion);

        if (!Directory.Exists(packagePath))
        {
            if (verbose)
            {
                Console.WriteLine($"  Skip (not found): {libraryName}/{libraryVersion} at {packagePath}");
            }

            return (0, 1);
        }

        var copiedCount = 0;
        var runtimeTargetsByFileName = BuildBestRuntimeTargetsByFileName(library.RuntimeTargets, "runtime", runtimeFallbacks);
        var nativeRuntimeTargetsByFileName = BuildBestRuntimeTargetsByFileName(library.RuntimeTargets, "native", runtimeFallbacks);

        copiedCount += CopyRuntimeAssemblies(library, packagePath, outputPath, runtimeTargetsByFileName, verbose);
        copiedCount += CopyRuntimeTargets(library, packagePath, outputPath, verbose);
        copiedCount += CopyResourceAssemblies(library, packagePath, outputPath, verbose);
        copiedCount += CopyNativeLibraries(library, packagePath, outputPath, nativeRuntimeTargetsByFileName, verbose);

        return (copiedCount, 0);
    }

    private static int CopyRuntimeAssemblies(
        LockFileTargetLibrary library,
        string packagePath,
        string outputPath,
        IReadOnlyDictionary<string, LockFileRuntimeTarget> runtimeTargetsByFileName,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var runtimeAssembly in library.RuntimeAssemblies)
        {
            if (IsPlaceholderPath(runtimeAssembly.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, runtimeAssembly.Path.Replace('/', Path.DirectorySeparatorChar));
            var fileName = Path.GetFileName(sourcePath);

            if (runtimeTargetsByFileName.TryGetValue(fileName, out var runtimeTarget))
            {
                sourcePath = Path.Combine(packagePath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
            }

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var destPath = Path.Combine(outputPath, fileName);
            if (CopyIfNewer(sourcePath, destPath, createDirectory: false))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy: {sourcePath} -> {destPath}");
                }
            }

            var xmlSourcePath = Path.ChangeExtension(sourcePath, ".xml");
            var xmlDestPath = Path.ChangeExtension(destPath, ".xml");
            if (File.Exists(xmlSourcePath) && CopyIfNewer(xmlSourcePath, xmlDestPath, createDirectory: false))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy (xml): {xmlSourcePath} -> {xmlDestPath}");
                }
            }
        }

        return copiedCount;
    }

    private static int CopyRuntimeTargets(
        LockFileTargetLibrary library,
        string packagePath,
        string outputPath,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var runtimeTarget in library.RuntimeTargets)
        {
            if (IsPlaceholderPath(runtimeTarget.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var destPath = Path.Combine(outputPath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
            if (CopyIfNewer(sourcePath, destPath, createDirectory: true))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy ({runtimeTarget.AssetType} target): {sourcePath} -> {destPath}");
                }
            }
        }

        return copiedCount;
    }

    private static int CopyResourceAssemblies(
        LockFileTargetLibrary library,
        string packagePath,
        string outputPath,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var resourceAssembly in library.ResourceAssemblies)
        {
            if (IsPlaceholderPath(resourceAssembly.Path))
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
            if (CopyIfNewer(sourcePath, destPath, createDirectory: true))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy (resource): {sourcePath} -> {destPath}");
                }
            }
        }

        return copiedCount;
    }

    private static int CopyNativeLibraries(
        LockFileTargetLibrary library,
        string packagePath,
        string outputPath,
        IReadOnlyDictionary<string, LockFileRuntimeTarget> nativeRuntimeTargetsByFileName,
        bool verbose)
    {
        var copiedCount = 0;
        var handledNativeRuntimeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nativeLib in library.NativeLibraries)
        {
            if (IsPlaceholderPath(nativeLib.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, nativeLib.Path.Replace('/', Path.DirectorySeparatorChar));
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
            if (CopyIfNewer(rootSourcePath, destPath, createDirectory: false))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy (native): {rootSourcePath} -> {destPath}");
                }
            }

            // Packages that only ship runtime-specific native assets are handled by the runtime target copy
            // loop above. This structured copy only applies when the package also carries a generic native path.
            var structuredDestPath = Path.Combine(outputPath, nativeLib.Path.Replace('/', Path.DirectorySeparatorChar));
            if (CopyIfNewer(sourcePath, structuredDestPath, createDirectory: true))
            {
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
            if (CopyIfNewer(sourcePath, destPath, createDirectory: false))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy (native target): {sourcePath} -> {destPath}");
                }
            }
        }

        return copiedCount;
    }

    private static bool CopyIfNewer(string sourcePath, string destPath, bool createDirectory)
    {
        if (File.Exists(destPath) &&
            File.GetLastWriteTimeUtc(sourcePath) <= File.GetLastWriteTimeUtc(destPath))
        {
            return false;
        }

        if (createDirectory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        }

        File.Copy(sourcePath, destPath, overwrite: true);
        return true;
    }

    private static bool IsPlaceholderPath(string path)
    {
        return string.Equals(Path.GetFileName(path), "_._", StringComparison.OrdinalIgnoreCase);
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
                !IsPlaceholderPath(runtimeTarget.Path))
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
