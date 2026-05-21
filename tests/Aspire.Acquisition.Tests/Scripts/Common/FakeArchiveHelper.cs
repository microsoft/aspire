// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Security.Cryptography;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Creates minimal tar.gz or zip archives containing a fake aspire binary, plus matching .sha512 checksum files.
/// </summary>
public static class FakeArchiveHelper
{
    public static async Task<FakeArchive> CreateFakeArchiveAsync(
        string outputDir,
        string platform = "linux-x64")
    {
        Directory.CreateDirectory(outputDir);

        var isWindows = platform.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        var extension = isWindows ? "zip" : "tar.gz";
        var archivePath = Path.Combine(outputDir, $"aspire-cli-{platform}.{extension}");
        var checksumPath = archivePath + ".sha512";

        var contentDir = Path.Combine(outputDir, $"archive-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentDir);

        try
        {
            var binaryName = isWindows ? "aspire.exe" : "aspire";
            var binaryPath = Path.Combine(contentDir, binaryName);
            await File.WriteAllTextAsync(binaryPath, "#!/bin/bash\necho \"aspire mock v1.0\"\n");

            if (!OperatingSystem.IsWindows())
            {
                FileHelper.MakeExecutable(binaryPath);
            }

            if (isWindows)
            {
                ZipFile.CreateFromDirectory(contentDir, archivePath);
            }
            else
            {
                await CreateTarGzAsync(contentDir, archivePath);
            }

            var hashHex = await ComputeSha512Async(archivePath);
            await File.WriteAllTextAsync(checksumPath, hashHex);

            return new FakeArchive(archivePath, checksumPath, hashHex);
        }
        finally
        {
            if (Directory.Exists(contentDir))
            {
                Directory.Delete(contentDir, recursive: true);
            }
        }
    }

    public static async Task<string> CreateFakeNupkgAsync(string outputDir, string packageName, string version)
    {
        Directory.CreateDirectory(outputDir);
        var filename = $"{packageName}.{version}.nupkg";
        var path = Path.Combine(outputDir, filename);
        await File.WriteAllTextAsync(path, "fake-nupkg-content");
        return path;
    }

    public static async Task<FakeArchive> CreateFakeArchiveWithBadChecksumAsync(string outputDir, string platform = "linux-x64")
    {
        var archive = await CreateFakeArchiveAsync(outputDir, platform);
        await File.WriteAllTextAsync(archive.ChecksumPath, "0000000000000000000000000000000000000000000000000000000000000000");
        return archive with { ChecksumHex = "0000000000000000000000000000000000000000000000000000000000000000" };
    }

    /// <summary>
    /// Creates a per-RID CLI archive whose fake aspire binary handles the commands
    /// verify-cli-archive.ps1 invokes (<c>aspire --version</c> and
    /// <c>aspire new aspire-starter --name NAME --output OUTPUT ...</c>). The fake
    /// binary is a bash script, so this is only usable on platforms where the
    /// verifier executes the binary directly (Linux/macOS).
    /// </summary>
    /// <param name="includeStraySidecar">When true, adds a <c>.aspire-install.json</c>
    /// at the archive root so the verifier's sidecar-rejection contract can be
    /// asserted; see <c>docs/specs/install-routes.md</c>.</param>
    /// <param name="nestAspireUnderSubdir">When true, places the <c>aspire</c> binary
    /// under a single subdirectory inside the archive rather than at the root. This
    /// exercises <c>Get-ArchiveRoot</c>'s single-subdirectory branch and confirms the
    /// sidecar scan still inspects the true archive root.</param>
    public static async Task<FakeArchive> CreateFakeVerifyArchiveAsync(
        string outputDir,
        string platform = "linux-x64",
        bool includeStraySidecar = false,
        bool nestAspireUnderSubdir = false)
    {
        Directory.CreateDirectory(outputDir);

        var extension = "tar.gz";
        var archivePath = Path.Combine(outputDir, $"aspire-cli-{platform}.{extension}");
        var checksumPath = archivePath + ".sha512";

        var contentDir = Path.Combine(outputDir, $"verify-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentDir);

        try
        {
            // When nesting, the binary lives at <contentDir>/payload/aspire, mirroring
            // archives whose producer wraps everything in a top-level directory.
            var binaryDir = nestAspireUnderSubdir
                ? Path.Combine(contentDir, "payload")
                : contentDir;
            Directory.CreateDirectory(binaryDir);

            var binaryPath = Path.Combine(binaryDir, "aspire");
            await File.WriteAllTextAsync(binaryPath, FakeVerifyAspireScript);
            FileHelper.MakeExecutable(binaryPath);

            if (includeStraySidecar)
            {
                // Per docs/specs/install-routes.md, per-RID archives must ship
                // sidecar-free. The verifier rejects archives that contain a
                // .aspire-install.json at the archive root.
                var sidecarPath = Path.Combine(contentDir, ".aspire-install.json");
                await File.WriteAllTextAsync(sidecarPath, "{\"source\":\"stray\"}");
            }

            await CreateTarGzAsync(contentDir, archivePath);

            var hashHex = await ComputeSha512Async(archivePath);
            await File.WriteAllTextAsync(checksumPath, hashHex);

            return new FakeArchive(archivePath, checksumPath, hashHex);
        }
        finally
        {
            if (Directory.Exists(contentDir))
            {
                Directory.Delete(contentDir, recursive: true);
            }
        }
    }

    // Minimal aspire mock for verify-cli-archive.ps1. Handles:
    //   aspire --version              -> print version, exit 0
    //   aspire new aspire-starter --name <n> --output <dir> [--non-interactive] [--nologo]
    //                                  -> mkdir "<dir>/<n>.AppHost", exit 0
    // Anything else exits non-zero so unexpected verifier invocations surface as test failures.
    private const string FakeVerifyAspireScript = """
        #!/usr/bin/env bash
        set -euo pipefail

        if [[ "${1:-}" == "--version" ]]; then
            echo "aspire mock v1.0"
            exit 0
        fi

        if [[ "${1:-}" == "new" ]]; then
            template="${2:-}"
            shift 2 || true

            name=""
            output=""
            while [[ $# -gt 0 ]]; do
                case "$1" in
                    --name)   name="$2";   shift 2 ;;
                    --output) output="$2"; shift 2 ;;
                    *)        shift ;;
                esac
            done

            if [[ "$template" != "aspire-starter" ]]; then
                echo "Unsupported template: $template" >&2
                exit 1
            fi

            mkdir -p "$output/$name.AppHost"
            exit 0
        fi

        echo "Unsupported command: $*" >&2
        exit 1

        """;

    private static async Task CreateTarGzAsync(string contentDir, string archivePath)
    {
        // Use TarWriter manually with AttributesToSkip = 0 instead of
        // TarFile.CreateFromDirectoryAsync, because the convenience API uses
        // default EnumerationOptions which skip files .NET considers hidden.
        // On Unix that includes dotfiles, so any dotfile entry under contentDir
        // would silently drop out of the archive.
        await using var fileStream = File.Create(archivePath);
        await using var gzipStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionLevel.Fastest);
        await using var tarWriter = new System.Formats.Tar.TarWriter(gzipStream, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true);

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            RecurseSubdirectories = true,
        };

        foreach (var fullPath in Directory.EnumerateFiles(contentDir, "*", options))
        {
            var relativePath = Path.GetRelativePath(contentDir, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            await tarWriter.WriteEntryAsync(fullPath, entryName: relativePath);
        }
    }

    private static async Task<string> ComputeSha512Async(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA512.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public record FakeArchive(string ArchivePath, string ChecksumPath, string ChecksumHex);
