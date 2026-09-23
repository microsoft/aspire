// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Aspire.Shared;

/// <summary>
/// Restricts filesystem-backed socket endpoints to the current user.
/// </summary>
internal static class SocketPermissionHelper
{
    // Reuse DirectoryHelper for Unix directory permissions, but keep socket-specific path
    // validation, Windows owner-only ACLs, and endpoint permissions here. DirectoryHelper
    // does not apply Windows ACLs or socket-file permissions.

    /// <summary>
    /// Creates or repairs a dedicated socket directory before any sockets are bound in it.
    /// </summary>
    internal static DirectoryInfo CreateDirectory(string path)
        => CreateDirectory(path, Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath());

    /// <summary>
    /// Creates or repairs a socket directory using explicitly supplied environment paths.
    /// </summary>
    internal static DirectoryInfo CreateDirectory(
        string path, string currentDirectory, string userProfileDirectory, string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(currentDirectory);
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);

        var directory = new DirectoryInfo(Path.GetFullPath(path, currentDirectory));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempDirectory, currentDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!IsSocketDirectory(directory, tempRoot, comparison) ||
            directory.Parent is null ||
            string.Equals(directory.FullName, Path.TrimEndingDirectorySeparator(currentDirectory), comparison) ||
            string.Equals(directory.FullName, Path.TrimEndingDirectorySeparator(userProfileDirectory), comparison) ||
            string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), tempRoot, comparison))
        {
            throw new IOException($"The socket directory '{path}' must use an Aspire socket directory layout (.aspire/cli/bch, .aspire/trmnl, or .aspire/pty).");
        }

        // Validate the entire configurable suffix, not just the leaf: .aspire or cli
        // could otherwise redirect chmod/ACL replacement into an unrelated directory.
        for (var current = directory; current is not null; current = current.Parent)
        {
            // System temporary roots can themselves be aliases (for example /var on macOS).
            if (string.Equals(current.FullName, tempRoot, comparison))
            {
                break;
            }

            if (current.LinkTarget is not null)
            {
                throw new IOException($"The socket directory '{path}' must not traverse a symbolic link.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsDirectory(directory);
        }

        // A configured endpoint must not cause chmod on a shared sticky directory such as /var/tmp.
        if (directory.Exists && (File.GetUnixFileMode(directory.FullName) & UnixFileMode.StickyBit) != 0)
        {
            throw new IOException($"The socket directory '{path}' must not be a shared sticky directory.");
        }

        return DirectoryHelper.CreateWithOwnerOnlyPermissions(directory.FullName);
    }

    private static bool IsSocketDirectory(DirectoryInfo directory, string tempRoot, StringComparison comparison)
    {
        var parent = directory.Parent;
        if (parent is null)
        {
            return false;
        }

        if (string.Equals(parent.Name, ".aspire", comparison))
        {
            return string.Equals(directory.Name, "trmnl", comparison) ||
                string.Equals(directory.Name, "pty", comparison);
        }

        if (string.Equals(directory.Name, "bch", comparison) &&
            string.Equals(parent.Name, "cli", comparison) &&
            string.Equals(parent.Parent?.Name, ".aspire", comparison))
        {
            return true;
        }

        // DCP session directories are allocated by ITempFileSystemService, not a socket override.
        return directory.Name.StartsWith("aspire-dcp", comparison) &&
            directory.Name.Length > "aspire-dcp".Length &&
            string.Equals(parent.FullName, tempRoot, comparison);
    }

    /// <summary>
    /// Secures the parent directory, binds the socket, and restricts its permissions before listening.
    /// </summary>
    internal static void Bind(Socket socket, string socketPath)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrEmpty(socketPath);

        var directory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The socket path must include a dedicated directory.", nameof(socketPath));
        }

        CreateDirectory(directory);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        if (!OperatingSystem.IsWindows())
        {
            // The directory is already private, including during the interval between bind and chmod.
            // Socket mode bits alone are not a portable access boundary on Unix.
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        // Windows socket files inherit the owner-only DACL from their secured parent directory.
    }

    [SupportedOSPlatform("windows")]
    private static DirectoryInfo CreateWindowsDirectory(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Supply the DACL at creation, then also replace permissions on directories from older runs.
        // Inheritable ACEs protect Windows AF_UNIX socket files without calling Unix-only APIs.
        directory.Create(security);
        directory.SetAccessControl(security);
        return directory;
    }
}
