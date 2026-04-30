// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Utils;

/// <summary>
/// Helpers for creating reparse points (symlinks on Unix, symlinks-or-junctions on
/// Windows) used to point stable public paths at versioned bundle directories.
/// </summary>
/// <remarks>
/// Windows strategy: prefer a symbolic link (<see cref="Directory.CreateSymbolicLink"/>)
/// — available to users with Developer Mode or admin — and fall back to a directory
/// junction (created via <c>DeviceIoControl</c> + <c>FSCTL_SET_REPARSE_POINT</c>)
/// when symlink creation is denied or the created symlink cannot be evaluated.
/// Junctions need no elevation, work for local directory targets, and are
/// transparent to <see cref="Directory.Exists(string)"/> and file enumeration.
///
/// Unix strategy: symbolic link via <see cref="Directory.CreateSymbolicLink"/>.
/// </remarks>
internal static partial class ReparsePoint
{
    /// <summary>
    /// Creates (or replaces) a directory reparse point at <paramref name="linkPath"/>
    /// whose target is <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// The target must be a local directory path. On Windows, if symbolic-link
    /// creation is denied or the created symbolic link cannot be evaluated, this
    /// method falls back to creating a directory junction. The public behavior is
    /// otherwise identical: the resulting path resolves to <paramref name="target"/>
    /// for I/O purposes.
    /// </remarks>
    /// <param name="linkPath">The path to create the reparse point at.</param>
    /// <param name="target">Path to the target directory. Relative paths are resolved against the link's parent directory.</param>
    public static void CreateOrReplace(string linkPath, string target)
    {
        if (string.IsNullOrEmpty(linkPath))
        {
            throw new ArgumentException("Link path is required.", nameof(linkPath));
        }

        if (string.IsNullOrEmpty(target))
        {
            throw new ArgumentException("Target path is required.", nameof(target));
        }

        var absoluteTarget = ResolveTargetPath(linkPath, target);

        // Create the new reparse point under a temporary name adjacent to the
        // final link, then atomically rename over the existing link. This avoids
        // any window where the public name does not exist.
        var tempLinkPath = GetTempLinkPath(linkPath);
        RemoveIfExists(tempLinkPath);

        CreateSymlinkOrJunction(tempLinkPath, absoluteTarget);

        try
        {
            // Guard against replacing a real directory on any platform. Callers must
            // remove or migrate existing real directories before calling this method.
            if (Exists(linkPath) && !IsReparsePoint(linkPath))
            {
                throw new InvalidOperationException(
                    $"Cannot replace '{linkPath}': it is a real directory, not a reparse point. " +
                    "Callers must remove or migrate existing directories before creating a reparse point.");
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows has no native overwriting rename for directory reparse points,
                // so remove the existing link then rename. The window is guarded by the
                // caller's bundle lock.
                RemoveIfExists(linkPath);
                Directory.Move(tempLinkPath, linkPath);
            }
            else
            {
                // On Unix, rename(2) atomically replaces an existing symlink with the
                // source symlink (both are treated as links, not directories, by the
                // kernel). This avoids any window where the public path is missing.
                if (NativeMethods.rename(tempLinkPath, linkPath) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw new IOException(
                        $"rename('{tempLinkPath}', '{linkPath}') failed with errno {errno}.");
                }
            }
        }
        catch
        {
            // If replacement fails, clean up the temporary link so the next attempt starts fresh.
            RemoveIfExists(tempLinkPath);
            throw;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="path"/> exists as a file
    /// or directory (resolving through reparse points).
    /// </summary>
    public static bool Exists(string path)
    {
        return Directory.Exists(path) || File.Exists(path);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="path"/> is a reparse
    /// point (symlink or junction).
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }

            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists)
            {
                return (dirInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>
    /// Resolves the immediate target of a reparse point. Returns <see langword="null"/>
    /// if <paramref name="path"/> is not a reparse point or the target cannot be read.
    /// </summary>
    public static string? GetTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
            {
                return null;
            }

            var dirInfo = new DirectoryInfo(path);
            if (!string.IsNullOrEmpty(dirInfo.LinkTarget))
            {
                return dirInfo.LinkTarget;
            }

            var fileInfo = new FileInfo(path);
            if (!string.IsNullOrEmpty(fileInfo.LinkTarget))
            {
                return fileInfo.LinkTarget;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Removes <paramref name="path"/> if it exists. Handles both regular directories,
    /// reparse points, and files. A reparse point is removed without following through
    /// to its target.
    /// </summary>
    public static void RemoveIfExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    Directory.Delete(path);
                }
                else
                {
                    File.Delete(path);
                }

                return;
            }
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists)
            {
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    // Deleting a directory reparse point removes the link without touching its target.
                    dirInfo.Delete(recursive: false);
                }
                else
                {
                    dirInfo.Delete(recursive: true);
                }

                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static void CreateSymlinkOrJunction(string linkPath, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return;
        }

        // Windows: try symbolic link first; fall back to a junction if creation is denied
        // or if Windows policy allows creation but prevents following this link type.
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            if (CanFollowDirectoryReparsePoint(linkPath))
            {
                return;
            }

            RemoveIfExists(linkPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            RemoveIfExists(linkPath);
            // Fall through to junction creation below.
        }

        CreateWindowsJunction(linkPath, target);
    }

    internal static bool CanFollowDirectoryReparsePoint(string path)
    {
        try
        {
            // Force Windows to evaluate the link immediately. Directory.Exists can
            // report true for a symlink whose evaluation class is disabled.
            _ = Directory.GetFileSystemEntries(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetTempLinkPath(string linkPath)
    {
        // Use an adjacent path under the same parent so the rename stays on-volume
        // and is effectively atomic.
        var parent = Path.GetDirectoryName(linkPath) ?? ".";
        var name = Path.GetFileName(linkPath);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(parent, $"{name}.new.{suffix}");
    }

    internal static string ResolveTargetPath(string linkPath, string target)
    {
        var normalizedTarget = NormalizeWindowsTargetPath(target);
        if (Path.IsPathFullyQualified(normalizedTarget))
        {
            return Path.GetFullPath(normalizedTarget);
        }

        var linkParent = Path.GetDirectoryName(Path.GetFullPath(linkPath)) ?? ".";
        return Path.GetFullPath(Path.Combine(linkParent, normalizedTarget));
    }

    private static string NormalizeWindowsTargetPath(string target)
    {
        const string ntLocalPathPrefix = @"\??\";
        if (OperatingSystem.IsWindows() &&
            target.StartsWith(ntLocalPathPrefix, StringComparison.Ordinal) &&
            target.Length > ntLocalPathPrefix.Length)
        {
            return target[ntLocalPathPrefix.Length..];
        }

        return target;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Windows junction fallback (no admin / dev-mode required)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Directly creates a Windows directory junction at <paramref name="linkPath"/>
    /// pointing to <paramref name="target"/>, bypassing the symbolic-link preference.
    /// Exposed for testing so the junction code path can be exercised even on
    /// systems where <see cref="Directory.CreateSymbolicLink"/> would succeed
    /// (e.g. Windows with Developer Mode enabled).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void CreateWindowsJunction(string linkPath, string target)
    {
        Directory.CreateDirectory(linkPath);

        try
        {
            // The substitute name for a mount-point junction must be an NT path
            // prefixed with "\??\" and target an absolute local directory.
            var substituteName = @"\??\" + target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var printName = target;

            using var handle = OpenReparsePointHandle(linkPath, write: true);
            WriteMountPointReparseData(handle, substituteName, printName);
        }
        catch
        {
            // If we failed mid-way, clean up the empty directory we just created
            // so subsequent attempts can start fresh.
            try
            {
                Directory.Delete(linkPath);
            }
            catch
            {
            }
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenReparsePointHandle(string path, bool write)
    {
        var access = write
            ? Win32Constants.GENERIC_READ | Win32Constants.GENERIC_WRITE
            : Win32Constants.GENERIC_READ;
        var handle = NativeMethods.CreateFileW(
            path,
            access,
            Win32Constants.FILE_SHARE_READ | Win32Constants.FILE_SHARE_WRITE | Win32Constants.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32Constants.OPEN_EXISTING,
            Win32Constants.FILE_FLAG_BACKUP_SEMANTICS | Win32Constants.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                $"Failed to open reparse point handle for '{path}'.");
        }

        return handle;
    }

    /// <summary>
    /// Maximum size of a reparse data buffer (defined by MAXIMUM_REPARSE_DATA_BUFFER_SIZE in the Windows SDK).
    /// </summary>
    private const int MaxReparseDataBufferSize = 16 * 1024; // 16 KB

    [SupportedOSPlatform("windows")]
    private static void WriteMountPointReparseData(SafeFileHandle handle, string substituteName, string printName)
    {
        var subNameBytes = System.Text.Encoding.Unicode.GetBytes(substituteName);
        var printNameBytes = System.Text.Encoding.Unicode.GetBytes(printName);

        // Layout (mount-point reparse buffer):
        //   DWORD ReparseTag
        //   WORD  ReparseDataLength
        //   WORD  Reserved
        //   WORD  SubstituteNameOffset
        //   WORD  SubstituteNameLength
        //   WORD  PrintNameOffset
        //   WORD  PrintNameLength
        //   WCHAR PathBuffer[...]
        //     - SubstituteName, NUL
        //     - PrintName, NUL

        var headerSize = 8; // tag + length + reserved
        var mountPointInfoSize = 8; // four WORDs
        var pathBufferSize = subNameBytes.Length + 2 + printNameBytes.Length + 2;
        var reparseDataLength = mountPointInfoSize + pathBufferSize;
        var totalSize = headerSize + reparseDataLength;

        if (reparseDataLength > ushort.MaxValue)
        {
            throw new PathTooLongException(
                $"Junction target path is too long. The reparse data ({reparseDataLength} bytes) exceeds the " +
                $"maximum of {ushort.MaxValue} bytes. Use a shorter target path.");
        }

        if (totalSize > MaxReparseDataBufferSize)
        {
            throw new PathTooLongException(
                $"Junction target path is too long. The reparse buffer ({totalSize} bytes) exceeds the " +
                $"maximum of {MaxReparseDataBufferSize} bytes. Use a shorter target path.");
        }

        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[..4], Win32Constants.IO_REPARSE_TAG_MOUNT_POINT);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), (ushort)reparseDataLength);
        // Reserved remains zero.

        var pathOffset = headerSize + mountPointInfoSize;
        var subNameOffset = 0;
        var subNameLength = subNameBytes.Length;
        var printNameOffset = subNameLength + 2;
        var printNameLength = printNameBytes.Length;

        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), (ushort)subNameOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10, 2), (ushort)subNameLength);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), (ushort)printNameOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14, 2), (ushort)printNameLength);

        subNameBytes.CopyTo(span[pathOffset..]);
        // 2 NUL bytes after substitute name already zero in buffer.
        printNameBytes.CopyTo(span[(pathOffset + printNameOffset)..]);

        if (!NativeMethods.DeviceIoControl(
                handle,
                Win32Constants.FSCTL_SET_REPARSE_POINT,
                buffer,
                (uint)totalSize,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "FSCTL_SET_REPARSE_POINT failed while creating directory junction.");
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // POSIX rename(2): atomic on a single filesystem, overwrites destination if it
        // exists and is of a compatible kind (symlink/file replacing symlink/file).
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8, EntryPoint = "rename")]
        internal static partial int rename(string oldpath, string newpath);
    }

    /// <summary>
    /// Reads the reparse tag (e.g. <see cref="Win32Constants.IO_REPARSE_TAG_MOUNT_POINT"/>
    /// or <see cref="Win32Constants.IO_REPARSE_TAG_SYMLINK"/>) from an existing reparse
    /// point. Returns <see langword="null"/> if <paramref name="path"/> is not a reparse
    /// point or the tag cannot be read.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static uint? GetReparseTag(string path)
    {
        if (!IsReparsePoint(path))
        {
            return null;
        }

        using var handle = OpenReparsePointHandle(path, write: false);

        // REPARSE_DATA_BUFFER starts with a DWORD ReparseTag; we only need the
        // first 4 bytes but must supply a buffer large enough for the ioctl.
        var buffer = new byte[16 * 1024];
        if (!NativeMethods.DeviceIoControl(
                handle,
                Win32Constants.FSCTL_GET_REPARSE_POINT,
                IntPtr.Zero,
                0,
                buffer,
                (uint)buffer.Length,
                out _,
                IntPtr.Zero))
        {
            return null;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
    }
}
