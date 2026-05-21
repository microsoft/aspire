// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Hosting;

namespace Aspire.Managed.NuGet.Commands;

/// <summary>
/// Manifest command - creates a package asset probe manifest from a project.assets.json file.
/// </summary>
public static class ManifestCommand
{
    /// <summary>
    /// Creates the manifest command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("manifest", "Create package asset probe manifest from project.assets.json");

        var assetsOption = new Option<string>("--assets", "-a")
        {
            Description = "Path to project.assets.json file",
            Required = true
        };
        command.Options.Add(assetsOption);

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output path for the package probe manifest",
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

            return ExecuteManifestAsync(assetsPath, outputPath, framework, runtimeIdentifier, verbose, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteManifestAsync(
        string assetsPath,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        bool verbose,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = NuGetPackageAssetResolver.Resolve(
                assetsPath,
                framework,
                runtimeIdentifier,
                verbose ? Console.WriteLine : null);

            if (verbose)
            {
                Console.WriteLine($"Using packages path: {resolution.PackagesPath}");
                Console.WriteLine($"Target framework: {resolution.TargetFramework}");
                Console.WriteLine($"Runtime identifier: {resolution.RuntimeIdentifier}");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Libraries: {0}", resolution.LibraryCount));
            }

            var manifest = CreateManifest(resolution.Assets);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await IntegrationPackageProbeManifest.WriteAsync(outputPath, manifest, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Manifest created: {0} managed assemblies and {1} native libraries written to {2}",
                manifest.ManagedAssemblies.Count,
                manifest.NativeLibraries.Count,
                outputPath));

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

    internal static IntegrationPackageProbeManifest CreateManifest(IEnumerable<NuGetPackageAsset> assets)
    {
        var managedAssemblies = new List<IntegrationPackageManagedAssembly>();
        var nativeLibraries = new List<IntegrationPackageNativeLibrary>();

        foreach (var asset in assets)
        {
            if (asset.IsManagedAssembly)
            {
                managedAssemblies.Add(new IntegrationPackageManagedAssembly
                {
                    Name = Path.GetFileNameWithoutExtension(asset.RelativePath),
                    Culture = asset.Culture,
                    Path = asset.SourcePath
                });
            }

            if (asset.IsNativeLibrary)
            {
                nativeLibraries.Add(new IntegrationPackageNativeLibrary
                {
                    FileName = Path.GetFileName(asset.RelativePath),
                    Path = asset.SourcePath
                });
            }
        }

        return IntegrationPackageProbeManifest.Create(managedAssemblies, nativeLibraries);
    }
}
