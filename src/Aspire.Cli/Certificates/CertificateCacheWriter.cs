// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Certificates;

internal static class CertificateCacheWriter
{
    private const UnixFileMode DirectoryPermissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FilePermissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void WriteFile(string outputPath, ReadOnlySpan<byte> contents, ILogger logger)
    {
        EnsureDirectory(Path.GetDirectoryName(outputPath)!);

        // Publish a fully flushed file atomically so readers never observe a partial certificate bundle.
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None
            };

            if (!OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416 // Validate platform compatibility
                options.UnixCreateMode = FilePermissions;
#pragma warning restore CA1416 // Validate platform compatibility
            }

            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, outputPath);
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                // Another process published the same content-addressed file first.
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed to delete temporary certificate cache file {TemporaryPath}", temporaryPath);
            }
        }
    }

    private static void EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

#pragma warning disable CA1416 // Validate platform compatibility
        Directory.CreateDirectory(directory, DirectoryPermissions);
        File.SetUnixFileMode(directory, DirectoryPermissions);
#pragma warning restore CA1416 // Validate platform compatibility
    }
}
