// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aspire.Cli.Processes;

internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Unix implementation using posix_spawn with a new process group and stdio bound to /dev/null.
    /// </summary>
    private static IDetachedProcess StartUnix(string fileName, IReadOnlyList<string> arguments, string workingDirectory, Func<string, bool>? shouldRemoveEnvironmentVariable, IReadOnlyDictionary<string, string>? additionalEnvironmentVariables)
    {
        const string shellPath = "/bin/sh";
        const string nullDevice = "/dev/null";

        using var fileNameString = new NativeUtf8String(fileName);
        using var shellPathString = new NativeUtf8String(shellPath);
        using var nullDeviceString = new NativeUtf8String(nullDevice);
        using var workingDirectoryString = new NativeUtf8String(workingDirectory);
        using var environment = NativeStringArray.CreateEnvironment(shouldRemoveEnvironmentVariable, additionalEnvironmentVariables);
        using var fileActions = new PosixSpawnFileActions();
        using var attributes = new PosixSpawnAttributes();

        fileActions.AddOpen(StandardInputFileDescriptor, nullDeviceString.Pointer, OpenReadOnly);
        fileActions.AddOpen(StandardOutputFileDescriptor, nullDeviceString.Pointer, OpenWriteOnly);
        fileActions.AddOpen(StandardErrorFileDescriptor, nullDeviceString.Pointer, OpenWriteOnly);
        attributes.SetNewProcessGroup();

        // Prefer spawning the requested executable directly. The cwd file action is a non-portable
        // libc extension (available on macOS and newer glibc), so keep the shell exec handoff as a
        // fallback for older glibc/musl systems without mutating this process's global cwd.
        var canSetWorkingDirectory = fileActions.TryAddChangeDirectory(workingDirectoryString.Pointer);
        using var argv = NativeStringArray.Create(canSetWorkingDirectory
            ? CreateDirectArguments(fileName, arguments)
            : CreateShellArguments(fileName, arguments, workingDirectory));

        int processId;
        var spawnResult = canSetWorkingDirectory
            ? posix_spawnp(
                out processId,
                fileNameString.Pointer,
                fileActions.Pointer,
                attributes.Pointer,
                argv.Pointer,
                environment.Pointer)
            : posix_spawn(
                out processId,
                shellPathString.Pointer,
                fileActions.Pointer,
                attributes.Pointer,
                argv.Pointer,
                environment.Pointer);
        if (spawnResult != 0)
        {
            throw CreatePosixSpawnException("posix_spawn", spawnResult);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return new PosixDetachedProcess(processId, process.StartTime);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The detached process exited before it could be observed.", ex);
        }
    }

    private static IReadOnlyList<string> CreateDirectArguments(string fileName, IReadOnlyList<string> arguments)
    {
        var directArguments = new List<string>(arguments.Count + 1)
        {
            fileName
        };

        directArguments.AddRange(arguments);
        return directArguments;
    }

    private static IReadOnlyList<string> CreateShellArguments(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var shellArguments = new List<string>(arguments.Count + 6)
        {
            "sh",
            "-c",
            "cd \"$1\" || exit 127; shift; exec \"$@\"",
            "aspire-detached",
            workingDirectory,
            fileName
        };

        shellArguments.AddRange(arguments);
        return shellArguments;
    }

    private static Win32Exception CreatePosixSpawnException(string operation, int errorCode) =>
        new(errorCode, $"Failed to invoke {operation} while starting detached process.");

    private sealed class PosixSpawnFileActions : IDisposable
    {
        // posix_spawn_file_actions_t is intentionally opaque. macOS defines it as a pointer-sized
        // handle, while glibc and musl currently expose an 80-byte struct on 64-bit platforms.
        // Allocate a conservative native buffer and let libc initialize/destroy its representation.
        private const int BufferSize = 256;

        public PosixSpawnFileActions()
        {
            Pointer = Marshal.AllocHGlobal(BufferSize);

            var result = posix_spawn_file_actions_init(Pointer);
            if (result != 0)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
                throw CreatePosixSpawnException("posix_spawn_file_actions_init", result);
            }
        }

        public IntPtr Pointer { get; private set; }

        public void AddOpen(int descriptor, IntPtr path, int flags)
        {
            var result = posix_spawn_file_actions_addopen(Pointer, descriptor, path, flags, mode: 0);
            if (result != 0)
            {
                throw CreatePosixSpawnException("posix_spawn_file_actions_addopen", result);
            }
        }

        public bool TryAddChangeDirectory(IntPtr path)
        {
            if (s_posixSpawnFileActionsAddChangeDirectory is not { } addChangeDirectory)
            {
                return false;
            }

            var result = addChangeDirectory(Pointer, path);
            if (result != 0)
            {
                throw CreatePosixSpawnException("posix_spawn_file_actions_addchdir_np", result);
            }

            return true;
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
            {
                return;
            }

            _ = posix_spawn_file_actions_destroy(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class PosixSpawnAttributes : IDisposable
    {
        private const int BufferSize = 1024;

        public PosixSpawnAttributes()
        {
            Pointer = Marshal.AllocHGlobal(BufferSize);

            var result = posix_spawnattr_init(Pointer);
            if (result != 0)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
                throw CreatePosixSpawnException("posix_spawnattr_init", result);
            }
        }

        public IntPtr Pointer { get; private set; }

        public void SetNewProcessGroup()
        {
            var setGroupResult = posix_spawnattr_setpgroup(Pointer, pgroup: 0);
            if (setGroupResult != 0)
            {
                throw CreatePosixSpawnException("posix_spawnattr_setpgroup", setGroupResult);
            }

            // POSIX_SPAWN_SETPGROUP with pgroup 0 makes the child the root of a new process
            // group, so later cleanup of the launcher's process group does not signal it.
            // See https://pubs.opengroup.org/onlinepubs/9799919799/functions/posix_spawnattr_setpgroup.html.
            var setFlagsResult = posix_spawnattr_setflags(Pointer, PosixSpawnSetProcessGroup);
            if (setFlagsResult != 0)
            {
                throw CreatePosixSpawnException("posix_spawnattr_setflags", setFlagsResult);
            }
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
            {
                return;
            }

            _ = posix_spawnattr_destroy(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    private sealed class NativeUtf8String : IDisposable
    {
        public NativeUtf8String(string value)
        {
            Pointer = Marshal.StringToCoTaskMemUTF8(value);
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(Pointer);
        }
    }

    private sealed class NativeStringArray : IDisposable
    {
        private readonly NativeUtf8String[] _strings;

        private NativeStringArray(NativeUtf8String[] strings, IntPtr pointer)
        {
            _strings = strings;
            Pointer = pointer;
        }

        public IntPtr Pointer { get; }

        public static NativeStringArray Create(IReadOnlyList<string> values)
        {
            var strings = values.Select(static value => new NativeUtf8String(value)).ToArray();
            var pointer = Marshal.AllocHGlobal(IntPtr.Size * (strings.Length + 1));

            for (var i = 0; i < strings.Length; i++)
            {
                Marshal.WriteIntPtr(pointer, i * IntPtr.Size, strings[i].Pointer);
            }

            Marshal.WriteIntPtr(pointer, strings.Length * IntPtr.Size, IntPtr.Zero);
            return new NativeStringArray(strings, pointer);
        }

        public static NativeStringArray CreateEnvironment(Func<string, bool>? shouldRemoveEnvironmentVariable, IReadOnlyDictionary<string, string>? additionalEnvironmentVariables)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && (shouldRemoveEnvironmentVariable is null || !shouldRemoveEnvironmentVariable(key)))
                {
                    values[key] = entry.Value as string ?? string.Empty;
                }
            }

            if (additionalEnvironmentVariables is not null)
            {
                foreach (var (key, value) in additionalEnvironmentVariables)
                {
                    values[key] = value;
                }
            }

            return Create(values.Select(static kvp => $"{kvp.Key}={kvp.Value}").ToArray());
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            foreach (var value in _strings)
            {
                value.Dispose();
            }
        }
    }

    private sealed class PosixDetachedProcess : IDetachedProcess
    {
        private readonly Task<int> _exitCodeTask;

        public PosixDetachedProcess(int processId, DateTime startTime)
        {
            Id = processId;
            StartTime = startTime;
            _exitCodeTask = Task.Run(() => WaitForExit(processId));
        }

        public int Id { get; }

        public bool HasExited => _exitCodeTask.IsCompleted;

        public int ExitCode
        {
            get
            {
                if (!_exitCodeTask.IsCompleted)
                {
                    throw new InvalidOperationException("Process must exit before requested information can be determined.");
                }

                return _exitCodeTask.GetAwaiter().GetResult();
            }
        }

        public DateTime StartTime { get; }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _exitCodeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Kill(bool entireProcessTree)
        {
            var pid = entireProcessTree ? -Id : Id;
            if (sys_kill(pid, SigKill) != 0 && Marshal.GetLastPInvokeError() != Esrch)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to kill detached process.");
            }
        }

        public void Dispose()
        {
        }

        private static int WaitForExit(int processId)
        {
            while (true)
            {
                var result = waitpid(processId, out var status, 0);
                if (result == processId)
                {
                    return GetPosixExitCode(status);
                }

                if (Marshal.GetLastPInvokeError() != Eintr)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to wait for detached process.");
                }
            }
        }

        private static int GetPosixExitCode(int status)
        {
            if ((status & 0x7f) == 0)
            {
                return (status >> 8) & 0xff;
            }

            return 128 + (status & 0x7f);
        }
    }

    private const int StandardInputFileDescriptor = 0;
    private const int StandardOutputFileDescriptor = 1;
    private const int StandardErrorFileDescriptor = 2;
    private const int Eintr = 4;
    private const int Esrch = 3;
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int SigKill = 9;
    private const short PosixSpawnSetProcessGroup = 0x02;
    private static readonly PosixSpawnFileActionsAddChangeDirectoryDelegate? s_posixSpawnFileActionsAddChangeDirectory = ResolvePosixSpawnFileActionsAddChangeDirectory();

    private static PosixSpawnFileActionsAddChangeDirectoryDelegate? ResolvePosixSpawnFileActionsAddChangeDirectory()
    {
        return NativeLibrary.TryLoad("libc", out var libcHandle)
            && NativeLibrary.TryGetExport(libcHandle, "posix_spawn_file_actions_addchdir_np", out var export)
            ? Marshal.GetDelegateForFunctionPointer<PosixSpawnFileActionsAddChangeDirectoryDelegate>(export)
            : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PosixSpawnFileActionsAddChangeDirectoryDelegate(IntPtr fileActions, IntPtr path);

    [LibraryImport("libc")]
    private static partial int posix_spawn(
        out int pid,
        IntPtr path,
        IntPtr fileActions,
        IntPtr attrp,
        IntPtr argv,
        IntPtr envp);

    [LibraryImport("libc")]
    private static partial int posix_spawnp(
        out int pid,
        IntPtr file,
        IntPtr fileActions,
        IntPtr attrp,
        IntPtr argv,
        IntPtr envp);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_addopen(IntPtr fileActions, int descriptor, IntPtr path, int flags, int mode);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(IntPtr fileActions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(IntPtr fileActions);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_destroy(IntPtr attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_init(IntPtr attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setpgroup(IntPtr attributes, int pgroup);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static partial int sys_kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);
}
