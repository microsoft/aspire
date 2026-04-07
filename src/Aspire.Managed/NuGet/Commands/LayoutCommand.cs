// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
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
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(ExecuteLayout(assetsPath, outputPath, framework, verbose));
        });

        return command;
    }

    private static int ExecuteLayout(
        string assetsPath,
        string outputPath,
        string framework,
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

            // Find the target for our framework
            var target = lockFile.Targets.FirstOrDefault(t =>
                t.TargetFramework.GetShortFolderName().Equals(framework, StringComparison.OrdinalIgnoreCase) ||
                t.TargetFramework.ToString().Equals(framework, StringComparison.OrdinalIgnoreCase));

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

                // Copy runtime assemblies
                foreach (var runtimeAssembly in library.RuntimeAssemblies)
                {
                    // Skip placeholder files
                    if (runtimeAssembly.Path.EndsWith("_._", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourcePath = Path.Combine(packagePath, runtimeAssembly.Path.Replace('/', Path.DirectorySeparatorChar));

                    // Early exit if source doesn't exist
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(sourcePath);
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

                // Also copy native libraries if present
                foreach (var nativeLib in library.NativeLibraries)
                {
                    var sourcePath = Path.Combine(packagePath, nativeLib.Path.Replace('/', Path.DirectorySeparatorChar));

                    // Early exit if source doesn't exist
                    if (!File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(sourcePath);
                    var destPath = Path.Combine(outputPath, fileName);

                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath))
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        copiedCount++;

                        if (verbose)
                        {
                            Console.WriteLine($"  Copy (native): {sourcePath} -> {destPath}");
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
}
