// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Provides helpers for creating directories with secure platform-specific permissions.
/// </summary>
internal static class DirectoryHelper
{
    // Unix 0700: only the owner can list entries, create or remove entries, and traverse the directory.
    // Directories require execute permission for traversal, including access to files within them.
    private const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates a directory and restricts access to the current user on Unix.
    /// </summary>
    internal static DirectoryInfo CreateWithOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(path);
        }

        // Apply 0700 while creating a new directory so there is no window with permissions derived from
        // the process umask. CreateDirectory doesn't change the mode when the directory already exists,
        // so SetUnixFileMode is also required to repair an existing permissive directory.
        var directory = Directory.CreateDirectory(path, OwnerOnlyMode);
        File.SetUnixFileMode(path, OwnerOnlyMode);
        return directory;
    }
}
