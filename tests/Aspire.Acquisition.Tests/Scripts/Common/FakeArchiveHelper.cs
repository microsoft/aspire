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
    /// Creates a bundle-shaped CLI archive so verify-cli-archive.ps1 can test the shipped archive
    /// contract without depending on signed build artifacts or running the real Aspire CLI.
    /// </summary>
    public static async Task<FakeArchive> CreateFakeBundleArchiveAsync(string outputDir, string platform = "linux-x64")
    {
        Directory.CreateDirectory(outputDir);

        var isWindows = platform.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        var extension = isWindows ? "zip" : "tar.gz";
        var archivePath = Path.Combine(outputDir, $"aspire-cli-{platform}.{extension}");
        var checksumPath = archivePath + ".sha512";

        var contentDir = Path.Combine(outputDir, $"bundle-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentDir);

        try
        {
            var cliBinaryName = isWindows ? "aspire.exe" : "aspire";
            var managedBinaryName = isWindows ? "aspire-managed.exe" : "aspire-managed";
            var dcpBinaryName = isWindows ? "dcp.exe" : "dcp";

            var cliBinaryPath = Path.Combine(contentDir, cliBinaryName);
            var managedDir = Directory.CreateDirectory(Path.Combine(contentDir, "managed"));
            var dcpDir = Directory.CreateDirectory(Path.Combine(contentDir, "dcp"));
            var wwwRootDir = Directory.CreateDirectory(Path.Combine(managedDir.FullName, "wwwroot"));

            await File.WriteAllTextAsync(cliBinaryPath, CreateFakeCliScript(isWindows));
            await File.WriteAllTextAsync(Path.Combine(managedDir.FullName, managedBinaryName), CreateFakeManagedScript(isWindows));
            await File.WriteAllTextAsync(Path.Combine(dcpDir.FullName, dcpBinaryName), CreateFakeDcpScript(isWindows));
            await File.WriteAllTextAsync(Path.Combine(wwwRootDir.FullName, "index.html"), "<html><body>dashboard</body></html>");

            if (!isWindows)
            {
                FileHelper.MakeExecutable(cliBinaryPath);
                FileHelper.MakeExecutable(Path.Combine(managedDir.FullName, managedBinaryName));
                FileHelper.MakeExecutable(Path.Combine(dcpDir.FullName, dcpBinaryName));
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

    private static string CreateFakeCliScript(bool isWindows)
    {
        return isWindows
            ? """
                @echo off
                setlocal
                if "%~1"=="--version" (
                    echo aspire mock v1.0
                    exit /b 0
                )
                if "%~1"=="new" goto :new
                echo Unsupported command: %* 1>&2
                exit /b 1

                :new
                set "template=%~2"
                set "name="
                set "output="
                shift
                shift
                :parse
                if "%~1"=="" goto :parsed
                if "%~1"=="--name" (
                    set "name=%~2"
                    shift
                    shift
                    goto :parse
                )
                if "%~1"=="--output" (
                    set "output=%~2"
                    shift
                    shift
                    goto :parse
                )
                shift
                goto :parse

                :parsed
                if not exist "%output%" mkdir "%output%"
                if "%template%"=="aspire-starter" (
                    mkdir "%output%\%name%.AppHost" >nul 2>&1
                    exit /b 0
                )
                if "%template%"=="aspire-ts-starter" (
                    mkdir "%output%\.modules" >nul 2>&1
                    mkdir "%output%\frontend\src" >nul 2>&1
                    mkdir "%output%\api\src" >nul 2>&1
                    > "%output%\apphost.ts" echo // apphost
                    > "%output%\.modules\aspire.ts" echo // generated sdk
                    > "%output%\package.json" echo {}
                    > "%output%\package-lock.json" echo {}
                    > "%output%\frontend\package.json" echo {}
                    > "%output%\frontend\package-lock.json" echo {}
                    > "%output%\frontend\src\main.tsx" echo // frontend
                    > "%output%\api\package.json" echo {}
                    > "%output%\api\src\index.ts" echo // api
                    > "%output%\aspire.config.json" echo {"sdk":{"version":"10.0.0-preview.1"}}
                    exit /b 0
                )
                echo Unsupported template: %template% 1>&2
                exit /b 1
                """
            : """
                #!/usr/bin/env bash
                set -euo pipefail

                if [[ "${1:-}" == "--version" ]]; then
                    echo "aspire mock v1.0"
                    exit 0
                fi

                if [[ "${1:-}" == "new" ]]; then
                    template="${2:-}"
                    shift 2

                    name=""
                    output=""
                    while [[ $# -gt 0 ]]; do
                        case "$1" in
                            --name)
                                name="$2"
                                shift 2
                                ;;
                            --output)
                                output="$2"
                                shift 2
                                ;;
                            *)
                                shift
                                ;;
                        esac
                    done

                    mkdir -p "$output"

                    case "$template" in
                        aspire-starter)
                            mkdir -p "$output/$name.AppHost"
                            ;;
                        aspire-ts-starter)
                            mkdir -p "$output/.modules" "$output/frontend/src" "$output/api/src"
                            printf '// apphost\n' > "$output/apphost.ts"
                            printf '// generated sdk\n' > "$output/.modules/aspire.ts"
                            printf '{}\n' > "$output/package.json"
                            printf '{}\n' > "$output/package-lock.json"
                            printf '{}\n' > "$output/frontend/package.json"
                            printf '{}\n' > "$output/frontend/package-lock.json"
                            printf '// frontend\n' > "$output/frontend/src/main.tsx"
                            printf '{}\n' > "$output/api/package.json"
                            printf '// api\n' > "$output/api/src/index.ts"
                            printf '{"sdk":{"version":"10.0.0-preview.1"}}\n' > "$output/aspire.config.json"
                            ;;
                        *)
                            echo "Unsupported template: $template" >&2
                            exit 1
                            ;;
                    esac

                    exit 0
                fi

                echo "Unsupported command: $*" >&2
                exit 1
                """;
    }

    private static string CreateFakeManagedScript(bool isWindows)
    {
        return isWindows
            ? """
                @echo off
                echo aspire-managed mock
                exit /b 0
                """
            : """
                #!/usr/bin/env bash
                set -euo pipefail
                echo "aspire-managed mock"
                """;
    }

    private static string CreateFakeDcpScript(bool isWindows)
    {
        return isWindows
            ? """
                @echo off
                echo dcp mock
                exit /b 0
                """
            : """
                #!/usr/bin/env bash
                set -euo pipefail
                echo "dcp mock"
                """;
    }

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
