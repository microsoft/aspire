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
    public static async Task<FakeArchive> CreateFakeArchiveAsync(string outputDir, string platform = "linux-x64")
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

    private static async Task CreateTarGzAsync(string contentDir, string archivePath)
    {
        await using var fileStream = File.Create(archivePath);
        await using var gzipStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionLevel.Fastest);
        await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(contentDir, gzipStream, includeBaseDirectory: false);
    }

    private static async Task<string> ComputeSha512Async(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA512.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public record FakeArchive(string ArchivePath, string ChecksumPath, string ChecksumHex);
