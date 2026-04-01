// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using NuGet.Frameworks;
using NuGet.ProjectModel;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Layout command - creates a flat DLL layout from a project.assets.json file.
/// This enables the AppHost Server to load integration assemblies via probing paths.
/// </summary>
public static class LayoutCommand
{
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
            var target = ResolveTarget(lockFile, framework, effectiveRuntimeIdentifier);

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

    private static LockFileTarget? ResolveTarget(LockFile lockFile, string framework, string runtimeIdentifier)
    {
        var nugetFramework = NuGetFramework.ParseFolder(framework);
        return lockFile.GetTarget(nugetFramework, runtimeIdentifier)
            ?? lockFile.GetTarget(nugetFramework, runtimeIdentifier: null);
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
        copiedCount += CopyRuntimeAssemblies(library.RuntimeAssemblies, packagePath, outputPath, verbose);
        copiedCount += CopyRuntimeTargets(library.RuntimeTargets, packagePath, outputPath, verbose);
        copiedCount += CopyResourceAssemblies(library.ResourceAssemblies, packagePath, outputPath, verbose);
        copiedCount += CopyNativeLibraries(library.NativeLibraries, packagePath, outputPath, verbose);

        return (copiedCount, 0);
    }

    private static int CopyRuntimeAssemblies(
        IEnumerable<LockFileItem> runtimeAssemblies,
        string packagePath,
        string outputPath,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var runtimeAssembly in runtimeAssemblies)
        {
            if (IsPlaceholderPath(runtimeAssembly.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, runtimeAssembly.Path.Replace('/', Path.DirectorySeparatorChar));
            var fileName = Path.GetFileName(sourcePath);
            var runtimePathToPreserve = runtimeAssembly.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
                ? runtimeAssembly.Path
                : null;

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

            if (runtimePathToPreserve is not null)
            {
                var structuredDestPath = Path.Combine(outputPath, runtimePathToPreserve.Replace('/', Path.DirectorySeparatorChar));
                if (CopyIfNewer(sourcePath, structuredDestPath, createDirectory: true))
                {
                    copiedCount++;

                    if (verbose)
                    {
                        Console.WriteLine($"  Copy (runtime path): {sourcePath} -> {structuredDestPath}");
                    }
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
        IEnumerable<LockFileRuntimeTarget> runtimeTargets,
        string packagePath,
        string outputPath,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var runtimeTarget in runtimeTargets)
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
        IEnumerable<LockFileItem> resourceAssemblies,
        string packagePath,
        string outputPath,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var resourceAssembly in resourceAssemblies)
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
        IEnumerable<LockFileItem> nativeLibraries,
        string packagePath,
        string outputPath,
        bool verbose)
    {
        var copiedCount = 0;

        foreach (var nativeLib in nativeLibraries)
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
            var destPath = Path.Combine(outputPath, fileName);
            if (CopyIfNewer(sourcePath, destPath, createDirectory: false))
            {
                copiedCount++;

                if (verbose)
                {
                    Console.WriteLine($"  Copy (native): {sourcePath} -> {destPath}");
                }
            }

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

}
