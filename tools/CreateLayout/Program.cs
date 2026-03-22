// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Tools.CreateLayout;

/// <summary>
/// Creates the Aspire bundle layout for distribution.
/// This tool assembles all components into a self-contained package.
/// </summary>
/// <remarks>
/// See docs/specs/bundle.md for the complete bundle specification and layout structure.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for the layout",
            Required = true
        };

        var artifactsOption = new Option<string>("--artifacts", "-a")
        {
            Description = "Path to build artifacts directory",
            Required = true
        };

        var ridOption = new Option<string>("--rid")
        {
            Description = "Runtime identifier",
            Required = true
        };

        var bundleVersionOption = new Option<string>("--bundle-version")
        {
            Description = "Version string for the layout",
            DefaultValueFactory = _ => "0.0.0-dev"
        };

        var archiveOption = new Option<bool>("--archive")
        {
            Description = "Create archive (zip/tar.gz) after building"
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable verbose output"
        };

        var rootCommand = new RootCommand("CreateLayout - Build Aspire bundle layout for distribution");
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(artifactsOption);
        rootCommand.Options.Add(ridOption);
        rootCommand.Options.Add(bundleVersionOption);
        rootCommand.Options.Add(archiveOption);
        rootCommand.Options.Add(verboseOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputPath = parseResult.GetValue(outputOption)!;
            var artifactsPath = parseResult.GetValue(artifactsOption)!;
            var rid = parseResult.GetValue(ridOption)!;
            var version = parseResult.GetValue(bundleVersionOption)!;
            var createArchive = parseResult.GetValue(archiveOption);
            var verbose = parseResult.GetValue(verboseOption);

            try
            {
                using var builder = new LayoutBuilder(outputPath, artifactsPath, rid, version, verbose);
                await builder.BuildAsync().ConfigureAwait(false);

                if (createArchive)
                {
                    await builder.CreateArchiveAsync().ConfigureAwait(false);
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
        });

        return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

}

/// <summary>
/// Builds the layout directory structure.
/// </summary>
internal sealed class LayoutBuilder : IDisposable
{
    private readonly string _outputPath;
    private readonly string _artifactsPath;
    private readonly string _rid;
    private readonly string _version;
    private readonly bool _verbose;

    public LayoutBuilder(string outputPath, string artifactsPath, string rid, string version, bool verbose)
    {
        _outputPath = Path.GetFullPath(outputPath);
        _artifactsPath = Path.GetFullPath(artifactsPath);
        _rid = rid;
        _version = version;
        _verbose = verbose;
    }

    public void Dispose()
    {
        // Nothing to dispose
    }

    public async Task BuildAsync()
    {
        Log($"Building layout for {_rid} version {_version}");
        Log($"Output: {_outputPath}");
        Log($"Artifacts: {_artifactsPath}");

        // Clean and create output directory
        if (Directory.Exists(_outputPath))
        {
            Directory.Delete(_outputPath, recursive: true);
        }
        Directory.CreateDirectory(_outputPath);

        // Copy components
        CopyManaged();
        await CopyDcpAsync().ConfigureAwait(false);

        Log("Layout build complete!");
    }

    private void CopyManaged()
    {
        Log("Copying aspire-managed...");

        var managedPublishPath = FindPublishPath("Aspire.Managed");
        if (managedPublishPath is null)
        {
            throw new InvalidOperationException("Aspire.Managed publish output not found.");
        }

        var managedDir = Path.Combine(_outputPath, "managed");
        Directory.CreateDirectory(managedDir);

        // Copy only the aspire-managed executable and required assets (wwwroot for Dashboard).
        // Skip other .exe files — they are native host stubs from referenced Exe projects
        // that leak into the publish output but are not needed (everything is in aspire-managed.exe).
        var isWindows = _rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        var managedExeName = isWindows ? "aspire-managed.exe" : "aspire-managed";

        var managedExePath = Path.Combine(managedPublishPath, managedExeName);
        if (!File.Exists(managedExePath))
        {
            throw new InvalidOperationException($"aspire-managed executable not found at {managedExePath}");
        }

        File.Copy(managedExePath, Path.Combine(managedDir, managedExeName), overwrite: true);

        // Copy wwwroot (required for Dashboard static web assets)
        var wwwrootPath = Path.Combine(managedPublishPath, "wwwroot");
        if (Directory.Exists(wwwrootPath))
        {
            CopyDirectory(wwwrootPath, Path.Combine(managedDir, "wwwroot"));
        }

        Log($"  Copied aspire-managed to managed/");
    }

    private Task CopyDcpAsync()
    {
        Log("Copying DCP...");

        // DCP comes from NuGet packages, look for it in the artifacts
        var dcpPath = FindDcpPath();
        if (dcpPath is null)
        {
            Log("  WARNING: DCP not found. Skipping.");
            return Task.CompletedTask;
        }

        var dcpDir = Path.Combine(_outputPath, "dcp");
        Directory.CreateDirectory(dcpDir);

        CopyDirectory(dcpPath, dcpDir);
        Log($"  Copied DCP to dcp");

        return Task.CompletedTask;
    }

    public async Task<string> CreateArchiveAsync()
    {
        var archiveName = $"aspire-{_version}-{_rid}";
        var archivePath = Path.Combine(Path.GetDirectoryName(_outputPath)!, archiveName + ".tar.gz");

        Log($"Creating archive: {archivePath}");

        if (OperatingSystem.IsWindows())
        {
            // Use .NET TarWriter + GZip on Windows (no system tar available)
            var fileStream = File.Create(archivePath);
            await using (fileStream.ConfigureAwait(false))
            {
                var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                await using (gzipStream.ConfigureAwait(false))
                {
                    var tarWriter = new System.Formats.Tar.TarWriter(gzipStream, leaveOpen: true);
                    await using (tarWriter.ConfigureAwait(false))
                    {
                        var topLevelDir = Path.GetFileName(_outputPath);
                        foreach (var filePath in Directory.EnumerateFiles(_outputPath, "*", SearchOption.AllDirectories))
                        {
                            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(_outputPath)!, filePath).Replace('\\', '/');
                            var dataStream = File.OpenRead(filePath);
                            await using (dataStream.ConfigureAwait(false))
                            {
                                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, relativePath)
                                {
                                    DataStream = dataStream
                                };
                                await tarWriter.WriteEntryAsync(entry).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Use tar for tar.gz
            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-czf \"{archivePath}\" -C \"{Path.GetDirectoryName(_outputPath)}\" \"{Path.GetFileName(_outputPath)}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            // Prevent macOS from including resource forks/extended attributes in the archive
            psi.Environment["COPYFILE_DISABLE"] = "1";

            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"Failed to create archive (exit code {process.ExitCode}): {stderr}");
                }
            }
        }

        Log($"Archive created: {archivePath}");
        return archivePath;
    }

    private string? FindPublishPath(string projectName)
    {
        // Look for publish output in standard locations
        // Order: RID-specific publish paths first (Release then Debug)
        var searchPaths = new[]
        {
            // RID-specific self-contained publish output (preferred for Aspire.Managed)
            Path.Combine(_artifactsPath, "bin", projectName, "Release", "net10.0", _rid, "publish"),
            Path.Combine(_artifactsPath, "bin", projectName, "Debug", "net10.0", _rid, "publish"),
            // Native AOT output
            Path.Combine(_artifactsPath, "bin", projectName, "Release", "net10.0", _rid, "native"),
            Path.Combine(_artifactsPath, "bin", projectName, "Debug", "net10.0", _rid, "native"),
            // Non-RID publish output
            Path.Combine(_artifactsPath, "bin", projectName, "Release", "net10.0", "publish"),
            Path.Combine(_artifactsPath, "bin", projectName, "Debug", "net10.0", "publish"),
        };

        foreach (var path in searchPaths)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private string? FindDcpPath()
    {
        // DCP is in NuGet packages as Microsoft.DeveloperControlPlane.{os}-{arch}
        var nugetPackages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");

        // Map RID to DCP package name format
        // win-x64 -> windows-amd64, linux-x64 -> linux-amd64, osx-arm64 -> darwin-arm64
        var dcpRid = _rid.ToLowerInvariant() switch
        {
            "win-x64" => "windows-amd64",
            "win-arm64" => "windows-arm64",
            "linux-x64" => "linux-amd64",
            "linux-arm64" => "linux-arm64",
            "linux-musl-x64" => "linux-musl-amd64",
            "osx-x64" => "darwin-amd64",
            "osx-arm64" => "darwin-arm64",
            _ => _rid.ToLowerInvariant()
        };

        var dcpPackageName = $"microsoft.developercontrolplane.{dcpRid}";
        var dcpPackagePath = Path.Combine(nugetPackages, dcpPackageName);

        if (Directory.Exists(dcpPackagePath))
        {
            // Find latest version (semantic versioning aware)
            var latestVersion = Directory.GetDirectories(dcpPackagePath)
                .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();

            if (latestVersion.Path is not null)
            {
                var toolsPath = Path.Combine(latestVersion.Path, "tools");
                if (Directory.Exists(toolsPath))
                {
                    Log($"  Found DCP at {toolsPath}");
                    return toolsPath;
                }
            }
        }

        // Fallback: try the old Aspire.Hosting.Orchestration.{rid} package name
        var oldPackageName = $"aspire.hosting.orchestration.{_rid.ToLowerInvariant()}";
        var oldPackagePath = Path.Combine(nugetPackages, oldPackageName);

        if (Directory.Exists(oldPackagePath))
        {
            var latestVersion = Directory.GetDirectories(oldPackagePath)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            if (latestVersion is not null)
            {
                var toolsPath = Path.Combine(latestVersion, "tools");
                if (Directory.Exists(toolsPath))
                {
                    Log($"  Found DCP at {toolsPath} (legacy package)");
                    return toolsPath;
                }
            }
        }

        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private void Log(string message)
    {
        if (_verbose || !message.StartsWith("  "))
        {
            Console.WriteLine(message);
        }
    }
}

#region JSON Models

[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
internal sealed partial class LayoutJsonContext : JsonSerializerContext
{
}

#endregion
