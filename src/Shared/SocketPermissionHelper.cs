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
    /// <summary>
    /// Creates or repairs a dedicated socket directory before any sockets are bound in it.
    /// </summary>
    internal static DirectoryInfo CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = new DirectoryInfo(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (directory.Parent is null ||
            string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), comparison))
        {
            throw new IOException($"The socket directory '{path}' must be a dedicated subdirectory, not a filesystem or temporary root.");
        }

        if (directory.LinkTarget is not null)
        {
            throw new IOException($"The socket directory '{path}' must not be a symbolic link.");
        }

        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsDirectory(directory);
        }

        // A configured endpoint must not cause chmod on a shared sticky directory such as /var/tmp.
        if (directory.Exists && (File.GetUnixFileMode(path) & UnixFileMode.StickyBit) != 0)
        {
            throw new IOException($"The socket directory '{path}' must not be a shared sticky directory.");
        }

        return DirectoryHelper.CreateWithOwnerOnlyPermissions(path);
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
