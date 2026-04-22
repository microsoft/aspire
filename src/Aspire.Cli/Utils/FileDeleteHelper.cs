// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Helpers for deleting files and directories that may be locked by another
/// process (e.g. <c>aspire-managed.exe</c> still running from a previous CLI
/// session).
///
/// On Windows a running process holds an exclusive lock on its executable,
/// so <see cref="Directory.Delete(string, bool)"/> throws
/// <see cref="UnauthorizedAccessException"/>. Rather than failing the entire
/// operation, these helpers rename the locked item to
/// <c>{name}.old.{tickcount}</c> and let the caller proceed. The stale
/// renamed items are best-effort cleaned up on subsequent invocations via
/// <see cref="TryCleanupOldItems"/>.
/// </summary>
internal static class FileDeleteHelper
{
    internal enum DeleteDirectoryResult
    {
        NotFound,
        Deleted,
        Renamed,
        Blocked
    }

    /// <summary>
    /// Attempts to delete a directory recursively. If deletion fails because
    /// a file inside the directory is locked by another process, the directory
    /// is renamed to <c>{path}.old.{tickcount}</c> so the caller can create a
    /// fresh replacement.
    /// </summary>
    internal static DeleteDirectoryResult TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return DeleteDirectoryResult.NotFound;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return DeleteDirectoryResult.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A process still has a file open inside this directory
            // (e.g. aspire-managed.exe from a previous CLI session).
            // Rename it so the caller can proceed with a fresh directory.
            try
            {
                var renamedPath = $"{path}.old.{Environment.TickCount64}";
                Directory.Move(path, renamedPath);
                return DeleteDirectoryResult.Renamed;
            }
            catch (Exception)
            {
                return DeleteDirectoryResult.Blocked;
            }
        }
    }

    /// <summary>
    /// Attempts to delete a file. If deletion fails because the file is locked
    /// by another process, the file is renamed to <c>{path}.old.{tickcount}</c>
    /// so the caller can write a fresh replacement. Returns
    /// <see langword="true"/> if the file was deleted or renamed,
    /// <see langword="false"/> if it did not exist.
    /// </summary>
    internal static bool TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                var renamedPath = $"{path}.old.{Environment.TickCount64}";
                File.Move(path, renamedPath);
            }
            catch (Exception)
            {
                // Rename also failed — nothing more we can do.
            }
        }

        return true;
    }

    /// <summary>
    /// Best-effort cleanup of stale <c>.old.{tickcount}</c> items (both files
    /// and directories) that were left behind by previous
    /// <see cref="TryDeleteDirectory"/> or <see cref="TryDeleteFile"/> calls.
    /// Items that are still locked are silently skipped — they will be retried
    /// on the next invocation.
    /// </summary>
    internal static void TryCleanupOldItems(string directory, string name)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var pattern = $"{name}.old.*";

        foreach (var oldDir in Directory.EnumerateDirectories(directory, pattern))
        {
            try
            {
                Directory.Delete(oldDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use — leave it for the next attempt
            }
        }

        foreach (var oldFile in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                File.Delete(oldFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use
            }
        }
    }
}
