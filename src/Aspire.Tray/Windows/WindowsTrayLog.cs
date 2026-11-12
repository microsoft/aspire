// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray;

/// <summary>
/// Owns the GUI's diagnostic streams independently of the launching terminal.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTrayLog : IDisposable
{
    private readonly TextWriter _originalOutput = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly TextWriter _writer;

    public static string LogPath => Path.Combine(WindowsSingleInstance.DirectoryPath, "aspire-tray.log");

    public WindowsTrayLog()
    {
        _writer = TextWriter.Synchronized(new TimestampedWriter(OpenLog()));
        Console.SetOut(_writer);
        Console.SetError(_writer);
    }

    public static void EnsureWritable()
    {
        using var stream = OpenLog();
    }

    private static FileStream OpenLog()
        => OpenLog(WindowsSingleInstance.DirectoryPath);

    internal static FileStream OpenLog(string directory)
    {
        directory = Path.GetFullPath(directory);
        var current = Path.GetPathRoot(directory)!;
        var parents = new List<SafeFileHandle>();
        try
        {
            parents.Add(OpenDirectory(current));
            foreach (var component in directory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                // Each ancestor is already open without write/delete sharing, preventing
                // renames and reparse-point mutation while resolving the next component.
                // New directories inherit the profile ACL, not a shared temporary ACL.
                if (!CreateDirectory(current, 0) && Marshal.GetLastPInvokeError() != 183 /* ERROR_ALREADY_EXISTS */)
                {
                    throw NativeIOException("Could not create the tray log directory.");
                }
                parents.Add(OpenDirectory(current));
            }

            // OPEN_REPARSE_POINT applies to the final component only, hence the locked
            // ancestor walk above. Validate this handle, never reopen the checked path.
            // FILE_APPEND_DATA (without FILE_WRITE_DATA) makes appends atomic at EOF.
            // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew
            var file = CreateFile(Path.Combine(directory, "aspire-tray.log"),
                0x0004 | 0x0080 /* FILE_APPEND_DATA | FILE_READ_ATTRIBUTES */,
                0x0001 | 0x0002 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, 0,
                4 /* OPEN_ALWAYS */, 0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, 0);
            try
            {
                ValidateHandle(file, isDirectory: false);
                return new FileStream(file, FileAccess.Write);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        finally
        {
            // Once the file is open, path changes cannot redirect writes on its handle.
            // Do not keep the entire profile directory tree locked for the GUI lifetime.
            foreach (var parent in parents)
            {
                parent.Dispose();
            }
        }
    }

    internal static SafeFileHandle OpenDirectory(string path)
    {
        // Attribute-only opens do not participate in Windows sharing checks. Request
        // directory-list access too so withholding write/delete sharing actually blocks
        // ancestor replacement and reparse mutation during the component walk.
        var handle = CreateFile(path, 0x0081 /* FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES */,
            0x0001 /* FILE_SHARE_READ */, 0, 3 /* OPEN_EXISTING */,
            0x02000000 | 0x00200000 /* FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT */, 0);
        try
        {
            ValidateHandle(handle, isDirectory: true);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateHandle(SafeFileHandle handle, bool isDirectory)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            throw NativeIOException("Could not open or inspect the tray log path.");
        }
        if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || ((information.Attributes & (uint)FileAttributes.Directory) != 0) != isDirectory
            || (!isDirectory && information.NumberOfLinks != 1))
        {
            throw new IOException("The tray log must use ordinary directories and a regular file without reparse points or hard links.");
        }
    }

    private static IOException NativeIOException(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, nint security);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    public void Dispose()
    {
        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _writer.Dispose();
    }

    private sealed class TimestampedWriter(FileStream stream) : StreamWriter(stream, new UTF8Encoding(false))
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine($"[{DateTimeOffset.UtcNow:O}] {value}");
            Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
