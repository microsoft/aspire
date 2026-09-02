// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Provides safe filesystem operations for scaffolded <c>.gitignore</c> files.
/// </summary>
internal static class GitIgnoreFile
{
    private static readonly TimeSpan s_atomicWriteTemporaryFileRetentionPeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// Determines whether the path itself is a symbolic link.
    /// </summary>
    internal static bool IsSymbolicLink(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileInfo(path).LinkTarget is not null;
    }

    /// <summary>
    /// Writes complete content to a sibling file before atomically replacing the destination.
    /// </summary>
    internal static async Task WriteAllTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (IsSymbolicLink(path))
        {
            throw new IOException(string.Format(
                CultureInfo.CurrentCulture,
                TemplatingStrings.GitIgnoreSymbolicLinkNotSupported,
                path));
        }

        ReclaimStaleAtomicWriteTemporaryFiles(path);

        if (File.Exists(path))
        {
            // Atomic rename is governed by directory permissions on Unix and could otherwise replace
            // a read-only file that the previous in-place write correctly refused to modify.
            using var permissionCheck = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        UnixFileMode? existingMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            existingMode = File.GetUnixFileMode(path);
        }

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows() && existingMode is not null)
            {
                File.SetUnixFileMode(temporaryPath, existingMode.Value);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the write or move failure. A leftover sibling temp file cannot affect the
                // destination because only the final atomic move publishes it.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the write or move failure for the same reason as an I/O cleanup failure.
            }
        }
    }

    private static void ReclaimStaleAtomicWriteTemporaryFiles(string path)
    {
        var destination = new FileInfo(path);
        var temporaryFilePrefix = destination.Name + ".tmp-";
        var cutoff = DateTime.UtcNow - s_atomicWriteTemporaryFileRetentionPeriod;
        string[] siblings;

        try
        {
            siblings = Directory.GetFiles(destination.DirectoryName!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var sibling in siblings)
        {
            var fileName = Path.GetFileName(sibling);
            if (!IsAtomicWriteTemporaryFileName(fileName, temporaryFilePrefix))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(sibling);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    File.GetLastWriteTimeUtc(sibling) > cutoff)
                {
                    continue;
                }

                // A writer owns its temporary file with FileShare.None. Opening the same way both
                // proves that no writer still owns the candidate and lets DeleteOnClose remove it
                // without a close-then-delete race. The age guard also protects the brief interval
                // after a writer closes the stream and before it publishes the file with File.Move.
                using var stream = new FileStream(
                    sibling,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Reclamation is best effort and must not replace the destination write's exception.
            }
        }
    }

    private static bool IsAtomicWriteTemporaryFileName(string fileName, string temporaryFilePrefix)
    {
        if (!fileName.StartsWith(temporaryFilePrefix, StringComparison.Ordinal) ||
            fileName.Length != temporaryFilePrefix.Length + 32)
        {
            return false;
        }

        foreach (var character in fileName.AsSpan(temporaryFilePrefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
