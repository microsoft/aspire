// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;

namespace Aspire.Shared;

/// <summary>
/// Allocates private temporary storage in a leased session under the configured Aspire home.
/// </summary>
internal sealed class PrivateTemporaryStorage : ITemporaryStorage, IDisposable
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly Lazy<(string Path, FileLock Lease)> _session;
    private bool _disposed;

    public PrivateTemporaryStorage(IConfiguration configuration)
    {
        _session = new(() => CreateSession(configuration));
    }

    public string GetTempPath()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session.Value.Path + Path.DirectorySeparatorChar;
    }

    public string GetTempFileName()
    {
        var path = Path.Combine(GetTempPath(), Path.GetRandomFileName());
        using var stream = CreateFile(path);
        return path;
    }

    public DirectoryInfo CreateTempSubdirectory(string? prefix = null)
    {
        prefix ??= "aspire-";
        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            prefix.Contains(Path.DirectorySeparatorChar) || prefix.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Temporary prefixes must not contain directory components.", nameof(prefix));
        }

        return CreatePrivateDirectory(Path.Combine(GetTempPath(), prefix + Path.GetRandomFileName()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session.IsValueCreated)
        {
            // Release the lease, but leave session contents for explicit cleanup in a later layer.
            _session.Value.Lease.Dispose();
        }
    }

    private static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static (string Path, FileLock Lease) CreateSession(IConfiguration configuration)
    {
        var configuredHome = configuration[AspireHomeDirectory.EnvironmentVariable];
        var home = AspireHomeDirectory.GetDefault(configuredHome, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        // Relative explicit overrides retain their existing cwd-relative meaning.
        // Do not turn a missing user profile into an implicit cwd-relative home.
        var root = GetRoot(string.IsNullOrWhiteSpace(configuredHome) ? home : Path.GetFullPath(home));
        var session = Path.Combine(root, "aspire-session-" + Path.GetRandomFileName());
        var lease = FileLock.AcquirePrivate(session + ".lock");
        try
        {
            CreatePrivateDirectory(session);
            if (configuration.GetBool("ASPIRE_PRESERVE_TEMP_FILES", defaultValue: false))
            {
                using var marker = CreateFile(Path.Combine(session, ".preserve"));
            }

            return (session, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static string GetRoot(string home)
    {
        if (!Path.IsPathFullyQualified(home) || !Directory.Exists(Path.GetDirectoryName(home)))
        {
            throw new IOException("Aspire temporary storage requires an existing absolute user home directory.");
        }

        ValidateRoot(home, allowReadAndTraverse: true);
        var root = Path.Combine(home, "tmp");
        ValidateRoot(root, allowReadAndTraverse: false);
        return root;
    }

    private static DirectoryInfo CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(path, DirectoryMode);
        }

        return CreateWindowsDirectory(path);
    }

    private static void ValidateRoot(string path, bool allowReadAndTraverse)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException($"Aspire temporary storage must not traverse a link at '{path}'.");
        }

        CreatePrivateDirectory(path);
        directory.Refresh();
        if (directory.LinkTarget is not null)
        {
            throw new IOException($"Aspire temporary storage must not traverse a link at '{path}'.");
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            var allowed = allowReadAndTraverse ? DirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute : DirectoryMode;
            if ((mode & ~allowed) != 0 || (mode & DirectoryMode) != DirectoryMode)
            {
                throw new IOException($"Unsupported permissions on Aspire temporary storage '{path}'. Set its permissions to 0700.");
            }

            // Only the owner (or a privileged administrator) can reapply this mode.
            File.SetUnixFileMode(path, mode);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DirectoryInfo CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Cannot determine the current Windows user.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.Create(security);
        var existing = directory.GetAccessControl();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!user.Equals(existing.GetOwner(typeof(SecurityIdentifier))) ||
            existing.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference) &&
                !system.Equals(rule.IdentityReference) && !administrators.Equals(rule.IdentityReference)))
        {
            throw new IOException($"Unsupported ownership or permissions on Aspire temporary storage '{path}'.");
        }

        return directory;
    }
}
