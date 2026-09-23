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

    private const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates a dedicated socket directory, repairing existing permissions only for Aspire-owned directories.
    /// </summary>
    internal static DirectoryInfo CreateDirectory(string path, bool repairExisting)
        => CreateDirectory(path, repairExisting, Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath());

    /// <summary>
    /// Creates or repairs a socket directory using explicitly supplied environment paths.
    /// </summary>
    internal static DirectoryInfo CreateDirectory(
        string path, bool repairExisting, string currentDirectory, string userProfileDirectory, string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(currentDirectory);
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);

        var directory = new DirectoryInfo(Path.GetFullPath(path, currentDirectory));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempDirectory, currentDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (directory.Parent is null ||
            string.Equals(directory.FullName, Path.TrimEndingDirectorySeparator(currentDirectory), comparison) ||
            string.Equals(directory.FullName, Path.TrimEndingDirectorySeparator(userProfileDirectory), comparison) ||
            string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), tempRoot, comparison))
        {
            throw new IOException($"The socket directory '{path}' must be a dedicated directory, not the working directory, user profile, filesystem root, or temporary root.");
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
            return CreateWindowsDirectory(directory, repairExisting);
        }

        // A configured endpoint must not cause chmod on a shared sticky directory such as /var/tmp.
        if (directory.Exists && (File.GetUnixFileMode(directory.FullName) & UnixFileMode.StickyBit) != 0)
        {
            throw new IOException($"The socket directory '{path}' must not be a shared sticky directory.");
        }

        if (repairExisting)
        {
            return DirectoryHelper.CreateWithOwnerOnlyPermissions(directory.FullName);
        }

        // CreateDirectory applies the mode only to a new directory. Do not chmod an
        // existing override, even if another caller created it concurrently.
        Directory.CreateDirectory(directory.FullName, OwnerOnlyMode);
        if (File.GetUnixFileMode(directory.FullName) != OwnerOnlyMode)
        {
            throw new IOException($"The configured socket directory '{path}' must have mode 0700. Set its permissions to 0700 or choose a new dedicated directory.");
        }

        return directory;
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

        // The allocator repairs Aspire-owned defaults. A listener must not infer ownership
        // from the path or rewrite an existing configured directory's permissions.
        CreateDirectory(directory, repairExisting: false);
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
    private static DirectoryInfo CreateWindowsDirectory(DirectoryInfo directory, bool repairExisting)
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

        // Supply the DACL at creation; existing overrides must be validated without rewriting it.
        // Inheritable ACEs protect Windows AF_UNIX socket files without calling Unix-only APIs.
        directory.Create(security);
        if (repairExisting)
        {
            directory.SetAccessControl(security);
        }
        else
        {
            var existingSecurity = directory.GetAccessControl();
            var rules = existingSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            // Hex1b can add SYSTEM to a PTY directory after startup. This does not grant
            // access to other ordinary users and must not prevent subsequent terminals.
            if (!existingSecurity.AreAccessRulesProtected ||
                !user.Equals(existingSecurity.GetOwner(typeof(SecurityIdentifier))) ||
                rules.Any(rule => rule.AccessControlType != AccessControlType.Allow ||
                    (!user.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))) ||
                !rules.Any(rule => user.Equals(rule.IdentityReference) &&
                    rule.FileSystemRights == FileSystemRights.FullControl &&
                    rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                    rule.PropagationFlags == PropagationFlags.None))
            {
                throw new IOException($"The configured socket directory '{directory.FullName}' must have a protected owner-only ACL with inheritable full control for the current user (SYSTEM is also allowed). Set those permissions or choose a new dedicated directory.");
            }
        }
        return directory;
    }
}
