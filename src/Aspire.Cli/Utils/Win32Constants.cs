// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Win32 constants used by P/Invoke call sites in the CLI.
/// </summary>
/// <remarks>
/// The .NET BCL hides these constants behind <c>internal</c> partial classes
/// (e.g. <c>Interop.Kernel32</c>), so consumers outside the runtime have to
/// declare their own. Centralising them here keeps magic numbers out of
/// individual call sites and makes them easier to share when new interop
/// is added.
/// </remarks>
internal static class Win32Constants
{
    // CreateFile - dwDesiredAccess
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;

    // CreateFile - dwShareMode
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;

    // CreateFile - dwCreationDisposition
    public const uint OPEN_EXISTING = 3;

    // CreateFile - dwFlagsAndAttributes
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // DeviceIoControl - dwIoControlCode
    public const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    public const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

    // Reparse-point tags (winnt.h)
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003u;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000Cu;
}
