// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.ComponentModel;
using System.Runtime.InteropServices;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Hosting;

internal static partial class BrowserLogsPipeBrowserProcessLauncher
{
    private static BrowserLogsPipeBrowserProcess StartPosix(string executablePath, IReadOnlyList<string> browserArguments)
    {
        var appToBrowser = PosixPipe.Invalid;
        var browserToApp = PosixPipe.Invalid;
        FileStream? browserInput = null;
        FileStream? browserOutput = null;

        try
        {
            appToBrowser = CreatePosixPipe();
            browserToApp = CreatePosixPipe();
            // Chromium reserves fd 3 for browser input and fd 4 for browser output. pipe() can legally return either
            // number for one of our source descriptors, so move accidental fd 3/4 allocations out of the way before the
            // child remaps them with dup2. Without this, closing a "source" descriptor could accidentally close the final
            // reserved descriptor the browser needs.
            MoveReservedPipeDescriptors(ref appToBrowser);
            MoveReservedPipeDescriptors(ref browserToApp);

            var arguments = CreatePipeArguments(browserArguments);
            using var executablePathString = new NativeUtf8String(executablePath);
            using var argv = NativeStringArray.Create([executablePath, .. arguments]);

            var setParentDeathSignal = OperatingSystem.IsLinux();
            var processId = fork();
            if (processId == -1)
            {
                throw CreatePosixException("fork");
            }

            if (processId == 0)
            {
                // Keep the child path as small as possible after fork: remap descriptors, close extra pipe ends, and exec.
                // Do not log, allocate, or touch shared runtime state here. If any native call fails, exit immediately so
                // the parent observes process termination instead of a half-configured child.
                ExecPosixChild(
                    executablePathString.Pointer,
                    argv.Pointer,
                    appToBrowser.Read,
                    browserToApp.Write,
                    appToBrowser.Write,
                    browserToApp.Read,
                    setParentDeathSignal);
            }

            ClosePosixDescriptor(ref appToBrowser.Read);
            ClosePosixDescriptor(ref browserToApp.Write);

            // After the fork, the parent owns the write side of appToBrowser and the read side of browserToApp. Wrap them
            // in FileStream so the rest of BrowserLogs can treat pipe CDP like any other async stream transport.
            browserInput = CreateFileStreamFromDescriptor(ref appToBrowser.Write, FileAccess.Write);
            browserOutput = CreateFileStreamFromDescriptor(ref browserToApp.Read, FileAccess.Read);
            var processTask = WaitForPosixProcessAsync(processId);

            return new BrowserLogsPipeBrowserProcess(
                processId,
                browserOutput,
                browserInput,
                processTask,
                new PosixProcessLifetime(processId, processTask));
        }
        catch
        {
            browserInput?.Dispose();
            browserOutput?.Dispose();
            appToBrowser.Dispose();
            browserToApp.Dispose();
            throw;
        }
    }

    private static void ExecPosixChild(
        IntPtr executablePath,
        IntPtr argv,
        int browserReadDescriptor,
        int browserWriteDescriptor,
        int parentWriteDescriptor,
        int parentReadDescriptor,
        bool setParentDeathSignal)
    {
        if (setParentDeathSignal)
        {
            // Linux can ask the kernel to signal the browser when the AppHost process dies without running managed
            // cleanup. This is intentionally best-effort: macOS has no equivalent, and Chromium may still decide how to
            // cascade the signal to its child process tree.
            if (prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0) == -1 ||
                getppid() == 1)
            {
                _exit(127);
            }
        }

        if (dup2(browserReadDescriptor, 3) == -1 ||
            dup2(browserWriteDescriptor, 4) == -1)
        {
            _exit(127);
        }

        ClosePosixDescriptorIfNot(browserReadDescriptor, 3);
        ClosePosixDescriptorIfNot(browserWriteDescriptor, 4);
        close(parentWriteDescriptor);
        close(parentReadDescriptor);

        execv(executablePath, argv);
        _exit(127);
    }

    private static FileStream CreateFileStreamFromDescriptor(ref int descriptor, FileAccess access)
    {
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        descriptor = -1;
        // Anonymous pipe descriptors are not opened with overlapped/async flags. FileStream still exposes Task-based
        // methods over synchronous handles, but the constructor must be told the underlying handle is synchronous.
        return new FileStream(handle, access, bufferSize: 16 * 1024, isAsync: false);
    }

    private static PosixPipe CreatePosixPipe()
    {
        var descriptors = new int[2];
        if (pipe(descriptors) == -1)
        {
            throw CreatePosixException("pipe");
        }

        return new PosixPipe(descriptors[0], descriptors[1]);
    }

    private static void MoveReservedPipeDescriptors(ref PosixPipe pipe)
    {
        pipe.Read = MoveReservedDescriptor(pipe.Read);
        pipe.Write = MoveReservedDescriptor(pipe.Write);
    }

    private static int MoveReservedDescriptor(int descriptor)
    {
        if (descriptor is not (3 or 4))
        {
            return descriptor;
        }

        var movedDescriptor = fcntl(descriptor, F_DUPFD, 5);
        if (movedDescriptor == -1)
        {
            throw CreatePosixException("fcntl");
        }

        close(descriptor);
        return movedDescriptor;
    }

    private static async Task<ProcessResult> WaitForPosixProcessAsync(int processId)
    {
        return await Task.Run(() =>
        {
            while (true)
            {
                var result = waitpid(processId, out var status, 0);
                if (result == processId)
                {
                    return new ProcessResult(GetPosixExitCode(status));
                }

                if (Marshal.GetLastPInvokeError() != EINTR)
                {
                    throw CreatePosixException("waitpid");
                }
            }
        }).ConfigureAwait(false);
    }

    private static int GetPosixExitCode(int status)
    {
        if ((status & 0x7f) == 0)
        {
            return (status >> 8) & 0xff;
        }

        return 128 + (status & 0x7f);
    }

    private static void ClosePosixDescriptor(ref int descriptor)
    {
        if (descriptor >= 0)
        {
            close(descriptor);
            descriptor = -1;
        }
    }

    private static void ClosePosixDescriptorIfNot(int descriptor, int reservedDescriptor)
    {
        if (descriptor != reservedDescriptor)
        {
            close(descriptor);
        }
    }

    private static Win32Exception CreatePosixException(string operation) =>
        new(Marshal.GetLastPInvokeError(), $"Failed to invoke {operation} while starting tracked browser CDP pipe.");

    private struct PosixPipe(int read, int write) : IDisposable
    {
        public static PosixPipe Invalid => new(-1, -1);

        public int Read = read;

        public int Write = write;

        public void Dispose()
        {
            ClosePosixDescriptor(ref Read);
            ClosePosixDescriptor(ref Write);
        }
    }

    private sealed class PosixProcessLifetime(int processId, Task<ProcessResult> processTask) : IBrowserLogsPipeBrowserProcessLifetime
    {
        public async ValueTask DisposeAsync()
        {
            if (processTask.IsCompleted)
            {
                return;
            }

            sys_kill(processId, SIGINT);
            try
            {
                await processTask.WaitAsync(s_processExitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                sys_kill(processId, SIGKILL);
                await processTask.ConfigureAwait(false);
            }
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

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            foreach (var value in _strings)
            {
                value.Dispose();
            }
        }
    }

    private const int EINTR = 4;
    private const int F_DUPFD = 0;
    private const int PR_SET_PDEATHSIG = 1;
    private const int SIGINT = 2;
    private const int SIGKILL = 9;
    private const int SIGTERM = 15;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldfd, int newfd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "execv")]
    private static partial int execv(IntPtr path, IntPtr argv);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fork();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getppid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pipe([Out] int[] pipefd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, int arg2, int arg3, int arg4, int arg5);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static partial int sys_kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "_exit")]
    private static partial void _exit(int status);
}
