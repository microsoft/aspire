// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Shared;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray;

[SupportedOSPlatform("macos")]
internal static partial class SingleInstance
{
    private const int WouldBlock = 35;
    private const int LockExclusiveNonblocking = 2 | 4;

    // Use the lock's user directory, not TMPDIR: terminal/editor launches can have
    // different temporary directories but must still activate the same global tray.
    public static string ActivationPipeName => Path.Combine(DirectoryPath, "control-v1.sock");

    public static string DirectoryPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Aspire", "Tray");

    public static FileStream? TryAcquire()
        => TryAcquire(DirectoryPath);

    internal static FileStream? TryAcquire(string directory)
    {
        // NativeAOT's Unix named Mutex implementation is process-local:
        // https://github.com/dotnet/runtime/issues/110348
        DirectoryHelper.CreateWithOwnerOnlyPermissions(directory);

        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(directory, "instance.lock"), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        }
        catch (IOException ex) when (ex.HResult == WouldBlock)
        {
            // FileShare.None uses flock; contention is surfaced as the raw Darwin EWOULDBLOCK errno.
            // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs
            return null;
        }

        // FileStream's advisory lock is best-effort on unsupported filesystems. Require flock
        // to succeed rather than silently allowing multiple tray instances in that case.
        if (Lock(stream.SafeFileHandle, LockExclusiveNonblocking) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            stream.Dispose();
            throw new Win32Exception(error, "Could not acquire the tray's single-instance lock.");
        }

        // Keep the file, even after exit. Unlinking it lets concurrent starters lock different
        // inodes. Closing the handle (including on a crash) releases the OS lock automatically.
        return stream;
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static partial int Lock(SafeFileHandle handle, int operation);
}
