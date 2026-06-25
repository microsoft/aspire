// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;

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
            var resolution = NuGetPackageAssetResolver.Resolve(
                assetsPath,
                framework,
                runtimeIdentifier,
                verbose ? Console.WriteLine : null);

            // Create output directory
            Directory.CreateDirectory(outputPath);

            var copiedCount = 0;

            if (verbose)
            {
                Console.WriteLine($"Using packages path: {resolution.PackagesPath}");
                Console.WriteLine($"Target framework: {resolution.TargetFramework}");
                Console.WriteLine($"Runtime identifier: {resolution.RuntimeIdentifier}");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Libraries: {0}", resolution.LibraryCount));
            }

            foreach (var asset in resolution.Assets)
            {
                var destPath = Path.Combine(outputPath, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (CopyIfNewer(asset.SourcePath, destPath, createDirectory: asset.RelativePath.Contains('/')))
                {
                    copiedCount++;

                    if (verbose)
                    {
                        Console.WriteLine($"  Copy: {asset.SourcePath} -> {destPath}");
                    }
                }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Layout created: {0} files copied to {1}", copiedCount, outputPath));
            if (resolution.SkippedPackageCount > 0 && verbose)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  ({0} packages skipped - not found in cache)", resolution.SkippedPackageCount));
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

}
