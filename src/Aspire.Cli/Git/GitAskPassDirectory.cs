// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Hosting.Backchannel;
using Aspire.Shared;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Git;

// The prototype is deliberately macOS-only: do not infer ownership from mode bits,
// or apply Darwin flags/stat layouts on another Unix platform.
[SupportedOSPlatform("macos")]
internal sealed partial class GitAskPassDirectory : IDisposable
{
    private const int NoFollow = 0x100;
    private const int DirectoryOnly = 0x100000;
    private const int CloseOnExec = 0x1000000;
    private readonly SafeFileHandle _parent;
    private readonly SafeFileHandle _directory;
    private readonly string _name;
    private readonly ulong _inode;
    private ulong? _socketInode;
    private ulong? _helperInode;

    private GitAskPassDirectory(SafeFileHandle parent, SafeFileHandle directory, string name, string path)
    {
        _parent = parent;
        _directory = directory;
        _name = name;
        _inode = Stat(directory).Inode;
        SocketPath = Path.Combine(path, BackchannelConstants.ComputeSocketFileName("git.askpass"));
        BackchannelConstants.ValidateSocketPathLength(SocketPath);
    }

    public string SocketPath { get; }
    public ulong Inode => _inode;

    public string CreateHelper(string command)
    {
        if (_helperInode is not null)
        {
            throw new InvalidOperationException("The Git credential callback helper is already configured.");
        }
        // Git executes GIT_ASKPASS as an executable path, not a command string.
        // Create the fixed launcher exclusively relative to the held directory.
        // No prompt text, credentials or callback authorization are written here.
        var descriptor = CreateAt(_directory, "askpass", 1 | 0x200 | 0x800 | NoFollow | CloseOnExec,
            0x1c0, 0, 0, 0, 0, 0x1c0);
        if (descriptor < 0)
        {
            throw Error();
        }
        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        var status = Stat(handle);
        _helperInode = status.Inode;
        if (status.Mode != 0x81c0 || status.Owner != GetEffectiveUserId() || status.LinkCount != 1)
        {
            throw new IOException("Git credential callback launcher must be a current-user, single-link file with mode 0700.");
        }
        using var stream = new FileStream(handle, FileAccess.Write);
        // "$@" passes Git's prompt as opaque argv, never shell syntax.
        stream.Write(System.Text.Encoding.UTF8.GetBytes($"#!/bin/sh\nexec {command} \"$@\"\n"));
        return Path.Combine(Path.GetDirectoryName(SocketPath)!, "askpass");
    }

    internal static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "git");

    public static GitAskPassDirectory Create()
    {
        // Validate existing ancestry before the shared allocator creates anything.
        // No chmod of an existing public directory, and no symlinks below (or in) home.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using (OpenDirectory(home))
        {
        }
        var aspire = Path.Combine(home, ".aspire");
        if (!Directory.Exists(aspire))
        {
            SocketPermissionHelper.CreateDirectory(aspire, repairExisting: false);
        }
        using (OpenDirectory(aspire))
        {
        }
        SocketPermissionHelper.CreateDirectory(RootPath, repairExisting: false);
        var parent = OpenDirectory(RootPath);
        var name = BackchannelConstants.CreateRandomIdentifier(32);
        var path = Path.Combine(RootPath, name);
        try
        {
            // mkdirat is exclusive: never adopt an existing operation directory.
            if (MakeDirectoryAt(parent, name, 0x1c0) != 0)
            {
                throw Error();
            }
            var descriptor = OpenAt(parent, name, NoFollow | DirectoryOnly | CloseOnExec);
            if (descriptor < 0)
            {
                throw Error();
            }
            var directory = new SafeFileHandle(descriptor, ownsHandle: true);
            try
            {
                return new GitAskPassDirectory(parent, directory, name, path);
            }
            catch
            {
                directory.Dispose();
                if (UnlinkAt(parent, name, 0x80) != 0)
                {
                    throw Error();
                }
                throw;
            }
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    public void VerifyBoundSocket()
    {
        var status = StatAt(_directory, Path.GetFileName(SocketPath));
        if (status.Mode != 0xc180 || status.Owner != GetEffectiveUserId())
        {
            throw new IOException("Git credential callback socket must be owned by the current user with mode 0600.");
        }
        _socketInode = status.Inode;
    }

    public static void BindInCurrentDirectory(Socket socket, string name, ulong expectedInode)
    {
        if (name.Length == 0 || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new IOException("Invalid Git credential callback socket name.");
        }
        using var directory = new SafeFileHandle(OpenAtRaw(-2, ".", NoFollow | DirectoryOnly | CloseOnExec), ownsHandle: true);
        var status = Stat(directory);
        if (status.Inode != expectedInode || status.Owner != GetEffectiveUserId() || status.Mode != 0x41c0)
        {
            throw new IOException("Git credential callback working directory does not match the private operation directory.");
        }
        // Only the isolated server calls this. A process-local creation mask gives
        // the socket mode 0600 at bind, without changing the parent CLI's umask.
        // The relative name is resolved against the child's held cwd, not replaceable
        // ancestors. No pathname-based chmod is ever performed.
        SetCreationMask(0x7f);
        socket.Bind(new NonDeletingUnixEndPoint(new UnixDomainSocketEndPoint(name)));
        var bound = StatAt(directory, name);
        if (bound.Mode != 0xc180 || bound.Owner != GetEffectiveUserId())
        {
            throw new IOException("Git credential callback socket was not created with mode 0600.");
        }
    }

    public static void ValidateEndpoint(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path ||
            Path.GetDirectoryName(Path.GetDirectoryName(path)) != RootPath)
        {
            throw new IOException("Invalid Git credential callback endpoint.");
        }
        var operation = Path.GetFileName(Path.GetDirectoryName(path)!);
        if (operation.Length != 32 || !operation.All(char.IsAsciiHexDigit))
        {
            throw new IOException("Invalid Git credential callback operation.");
        }
        BackchannelConstants.ValidateSocketPathLength(path);
        InspectSocket(path);
    }

    private static FileStatus InspectSocket(string path)
    {
        using var directory = OpenDirectory(Path.GetDirectoryName(path)!);
        if (File.GetUnixFileMode(Path.GetDirectoryName(path)!) !=
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        {
            throw new IOException("Git credential callback directories require mode 0700.");
        }
        var status = StatAt(directory, Path.GetFileName(path));
        if (status.Owner != GetEffectiveUserId() || status.Mode != 0xc180)
        {
            throw new IOException("Git credential callback sockets require current-user ownership and mode 0600.");
        }
        return status;
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var current = new SafeFileHandle(OpenAtRaw(-2, "/", NoFollow | DirectoryOnly | CloseOnExec), ownsHandle: true);
        try
        {
            foreach (var component in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var descriptor = OpenAt(current, component, NoFollow | DirectoryOnly | CloseOnExec);
                if (descriptor < 0)
                {
                    throw Error();
                }
                var next = new SafeFileHandle(descriptor, ownsHandle: true);
                current.Dispose();
                current = next;
                var status = Stat(current);
                // Root-owned ancestors are trusted only when ordinary users cannot replace children.
                if ((status.Owner != GetEffectiveUserId() && status.Owner != 0) || (status.Mode & 0x12) != 0)
                {
                    throw new IOException("Git credential callback paths must not traverse writable or foreign-owned directories.");
                }
            }
            if (Stat(current).Owner != GetEffectiveUserId())
            {
                throw new IOException("Git credential callback directories must belong to the current user.");
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            // Use the held directory, not a newly resolved pathname, for cleanup.
            // Refuse changed endpoints; never recursively delete an operation directory.
            if (_socketInode is { } socketInode)
            {
                if (StatAt(_directory, Path.GetFileName(SocketPath)).Inode != socketInode ||
                    UnlinkAt(_directory, Path.GetFileName(SocketPath), 0) != 0)
                {
                    throw Error();
                }
            }
            if (_helperInode is { } helperInode &&
                (StatAt(_directory, "askpass").Inode != helperInode || UnlinkAt(_directory, "askpass", 0) != 0))
            {
                throw Error();
            }
            if (StatAt(_parent, _name).Inode != _inode || UnlinkAt(_parent, _name, 0x80) != 0)
            {
                throw Error();
            }
        }
        finally
        {
            _directory.Dispose();
            _parent.Dispose();
        }
    }

    private static FileStatus Stat(SafeFileHandle handle)
    {
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? GetStatus64(handle, out var status) : GetStatus(handle, out status);
        if (result != 0)
        {
            throw Error();
        }
        return status;
    }

    private static FileStatus StatAt(SafeFileHandle directory, string name)
    {
        // AT_SYMLINK_NOFOLLOW inspects the directory entry itself, never the link target.
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? GetStatusAt64(directory, name, out var status, 0x20)
            : GetStatusAt(directory, name, out status, 0x20);
        if (result != 0)
        {
            throw Error();
        }
        return status;
    }

    private static IOException Error()
        => new("Could not secure or clean up the Git credential callback endpoint.",
            new Win32Exception(Marshal.GetLastPInvokeError()));

    // These fixed-arity openat calls never create files (O_CREAT is absent), so no
    // variadic mode argument is needed on Darwin arm64.
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtRaw(int directory, string path, int flags);
    // openat's variadic creation mode is in a register on Darwin x64 and on the
    // stack on arm64. Supply both positions, matching the tray's secure file opener.
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreateAt(SafeFileHandle directory, string path, int flags, uint mode,
        nint unused1, nint unused2, nint unused3, nint unused4, uint stackMode);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectoryAt(SafeFileHandle directory, string path, ushort mode);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(SafeFileHandle directory, string path, int flags);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "umask")]
    private static partial ushort SetCreationMask(ushort mask);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static partial int GetStatus(SafeFileHandle file, out FileStatus status);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int GetStatus64(SafeFileHandle file, out FileStatus status);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstatat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetStatusAt(SafeFileHandle file, string path, out FileStatus status, int flags);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstatat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetStatusAt64(SafeFileHandle file, string path, out FileStatus status, int flags);

    // Same Darwin stat64 layout used by the tray's descriptor-based log security.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileStatus
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort LinkCount;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(16)] public uint Owner;
    }

    // Socket.Bind(UnixDomainSocketEndPoint) arranges pathname-based deletion on
    // disposal. A regular EndPoint wrapper deliberately leaves deletion to the
    // parent's held-directory/inode checks, never a fresh resolution of old ancestors.
    // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Sockets/src/System/Net/Sockets/UnixDomainSocketEndPoint.cs
    private sealed class NonDeletingUnixEndPoint(UnixDomainSocketEndPoint endpoint) : EndPoint
    {
        public override AddressFamily AddressFamily => AddressFamily.Unix;
        public override SocketAddress Serialize()
        {
            // The runtime knows the native size for its built-in Unix endpoint,
            // but custom endpoints must provide an accept buffer large enough for
            // any sockaddr_un, not just this short filename.
            var serialized = endpoint.Serialize();
            var result = new SocketAddress(AddressFamily.Unix, BackchannelConstants.GetMaxSocketPathBytesIncludingNull() + 2);
            for (var index = 2; index < serialized.Size; index++)
            {
                result[index] = serialized[index];
            }
            return result;
        }
        public override EndPoint Create(SocketAddress socketAddress)
            => new NonDeletingUnixEndPoint((UnixDomainSocketEndPoint)endpoint.Create(socketAddress));
    }
}
