// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;

namespace Aspire.Cli.Utils;

/// <summary>
/// Shared utilities for extracting archive files (.zip and .tar.gz).
/// </summary>
internal static class ArchiveHelper
{
    /// <summary>
    /// Extracts an archive to the specified directory, supporting .zip and .tar.gz formats.
    /// .zip is used for Windows CLI downloads; .tar.gz for Unix.
    /// </summary>
    internal static async Task ExtractAsync(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZipSafe(archivePath, destinationPath);
        }
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractTarGzSafeAsync(archivePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException($"Unsupported archive format: {archivePath}");
        }
    }

    private static void ExtractZipSafe(string archivePath, string destinationPath)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(destinationPath, entry.FullName));
            if (!fullPath.StartsWith(normalizedDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !fullPath.Equals(normalizedDestination, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Zip entry '{entry.FullName}' would extract outside the destination directory.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (dir is not null)
                {
                    Directory.CreateDirectory(dir);
                }
                entry.ExtractToFile(fullPath, overwrite: true);
            }
        }
    }

    private static async Task ExtractTarGzSafeAsync(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);

        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        await using var tarReader = new TarReader(gzipStream);

        while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(destinationPath, entry.Name));

            // Guard against path traversal attacks (e.g., entries containing ".." segments)
            if (!fullPath.StartsWith(normalizedDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !fullPath.Equals(normalizedDestination, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Tar entry '{entry.Name}' would extract outside the destination directory.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(fullPath);
                    break;

                case TarEntryType.RegularFile:
                    var dir = Path.GetDirectoryName(fullPath);
                    if (dir is not null)
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await entry.ExtractToFileAsync(fullPath, overwrite: true, cancellationToken).ConfigureAwait(false);

                    // Preserve Unix file permissions from tar entry
                    if (!OperatingSystem.IsWindows() && entry.Mode != default)
                    {
                        File.SetUnixFileMode(fullPath, (UnixFileMode)entry.Mode);
                    }
                    break;

                case TarEntryType.SymbolicLink:
                    if (string.IsNullOrEmpty(entry.LinkName))
                    {
                        continue;
                    }
                    // Validate symlink target stays within the extraction directory
                    var linkTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, entry.LinkName));
                    if (!linkTarget.StartsWith(normalizedDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                        !linkTarget.Equals(normalizedDestination, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Symlink '{entry.Name}' targets '{entry.LinkName}' which resolves outside the destination directory.");
                    }
                    var linkDir = Path.GetDirectoryName(fullPath);
                    if (linkDir is not null)
                    {
                        Directory.CreateDirectory(linkDir);
                    }
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    File.CreateSymbolicLink(fullPath, entry.LinkName);
                    break;
            }
        }
    }
}
