// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray;

[SupportedOSPlatform("macos")]
internal static partial class MacTrayLog
{
    private const int NoFollow = 0x100;
    private const int DirectoryOnly = 0x100000;
    private const int CloseOnExec = 0x1000000;

    public static SafeFileHandle Open(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute log path is required.", nameof(path));
        }

        // Walk from an open root, not a checked pathname. Each component is opened relative
        // to the previous descriptor with O_NOFOLLOW; renaming a parent cannot redirect us.
        // https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/open.2.html
        var components = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            throw new ArgumentException("A log filename is required.", nameof(path));
        }
        var directory = OpenHandle(-2, "/", NoFollow | DirectoryOnly | CloseOnExec, 0);
        try
        {
            for (var index = 0; index < components.Length - 1; index++)
            {
                var next = OpenAt(directory, components[index], NoFollow | DirectoryOnly | CloseOnExec, 0);
                if (next < 0 && Marshal.GetLastPInvokeError() == 2)
                {
                    if (MakeDirectoryAt(directory, components[index], 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
                    {
                        throw Error("create the tray log directory");
                    }
                    next = OpenAt(directory, components[index], NoFollow | DirectoryOnly | CloseOnExec, 0);
                }
                if (next < 0)
                {
                    throw Error("open the tray log directory without following links");
                }
                directory.Dispose();
                directory = new SafeFileHandle(next, ownsHandle: true);
            }

            var parentStat = Stat(directory);
            if (parentStat.Owner != GetEffectiveUserId())
            {
                throw new IOException("The tray log directory must belong to the current user.");
            }
            if (ChangeMode(directory, 0x1c0) != 0)
            {
                throw Error("protect the tray log directory");
            }

            // O_WRONLY | O_APPEND | O_CREAT | O_NONBLOCK. Nonblocking avoids hanging on a
            // substituted FIFO before fstat rejects anything except a single-link regular file.
            var file = OpenHandle(directory.DangerousGetHandle().ToInt32(), components[^1],
                1 | 8 | 0x200 | 4 | NoFollow | CloseOnExec, 0x180);
            try
            {
                var stat = Stat(file);
                if ((stat.Mode & 0xf000) != 0x8000 || stat.LinkCount != 1 || stat.Owner != GetEffectiveUserId())
                {
                    throw new IOException("The tray log must be a regular, single-link file owned by the current user.");
                }
                if (ChangeMode(file, 0x180) != 0)
                {
                    throw Error("protect the tray log file");
                }
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        finally
        {
            directory.Dispose();
        }
    }

    private static SafeFileHandle OpenHandle(int directory, string name, int flags, ushort mode)
    {
        var descriptor = OpenAtRaw(directory, name, flags, mode, 0, 0, 0, 0, mode);
        if (descriptor < 0)
        {
            throw Error("open the tray log without following links");
        }
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static FileStatus Stat(SafeFileHandle handle)
    {
        // Darwin x64's unsuffixed fstat has the legacy inode layout; arm64 uses stat64.
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? GetStatus64(handle, out var status) : GetStatus(handle, out status);
        if (result != 0)
        {
            throw Error("inspect the tray log descriptor");
        }
        return status;
    }

    private static IOException Error(string operation)
        => new($"Could not {operation}.", new Win32Exception(Marshal.GetLastPInvokeError()));

    private static int OpenAt(SafeFileHandle directory, string path, int flags, ushort mode)
        => OpenAtNative(directory, path, flags, mode, 0, 0, 0, 0, mode);

    // openat is variadic. Darwin arm64 reads mode from the stack, while x64 reads
    // argument four. Supply both locations; the unused register arguments are ignored.
    // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtNative(SafeFileHandle directory, string path, int flags, uint mode,
        nint unused1, nint unused2, nint unused3, nint unused4, uint stackMode);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtRaw(int directory, string path, int flags, uint mode,
        nint unused1, nint unused2, nint unused3, nint unused4, uint stackMode);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectoryAt(SafeFileHandle directory, string path, ushort mode);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int ChangeMode(SafeFileHandle file, ushort mode);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static partial int GetStatus(SafeFileHandle file, out FileStatus status);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int GetStatus64(SafeFileHandle file, out FileStatus status);

    // Darwin's stat64 is 144 bytes on both supported 64-bit architectures.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileStatus
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort LinkCount;
        [FieldOffset(16)] public uint Owner;
    }
}
