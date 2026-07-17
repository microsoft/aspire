// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable SYSLIB1054 // Test project does not allow unsafe code required by LibraryImport.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Cli.Processes;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Processes;

public class DetachedProcessLauncherTests
{
    // Regression test for the duplicate-handle bug that broke `aspire start` on Windows:
    // DetachedProcessLauncher.StartWindows points both Stdout and Stderr at the same NUL
    // handle, and PROC_THREAD_ATTRIBUTE_HANDLE_LIST rejects duplicate handle values —
    // CreateProcessW returns ERROR_INVALID_PARAMETER (87). The unified
    // WindowsProcessInterop.SpawnProcess de-duplicates the inheritable
    // handle list, so this spawn must succeed.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Start_OnWindows_WithSharedStdoutStderrHandle_Succeeds()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        // A short-lived child is sufficient: we only need CreateProcessW to return successfully.
        // `cmd.exe /c exit 0` returns immediately and never touches stdout/stderr, so any
        // failure mode here is from the spawn primitive, not from the child itself.
        using var child = DetachedProcessLauncher.Start(
            "cmd.exe",
            ["/c", "exit", "0"],
            Environment.CurrentDirectory);

        Assert.True(child.Id > 0);
    }

    [Fact]
    public async Task Start_OnUnix_SurvivesLauncherProcessGroupCleanup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test.");

        var temporaryDirectory = Directory.CreateTempSubdirectory("aspire-detached-launcher-");
        var childPidFile = Path.Combine(temporaryDirectory.FullName, "child.pid");
        int? childPid = null;
        int? childProcessGroupId = null;
        RemoteInvokeHandle? launchCommand = null;

        try
        {
            launchCommand = RemoteExecutor.Invoke(static (pidFile, workingDirectory) =>
            {
                using var child = DetachedProcessLauncher.Start(
                    "/bin/sh",
                    ["-c", "sleep 30"],
                    workingDirectory);

                File.WriteAllText(pidFile, child.Id.ToString(CultureInfo.InvariantCulture));
            }, childPidFile, temporaryDirectory.FullName, new RemoteInvokeOptions { Start = false });

            // RemoteExecutor gives us the command line for an isolated helper process, but this
            // test must start that helper in its own process group. We own that spawned process
            // with posix_spawn/waitpid below instead of RemoteInvokeHandle.Dispose, whose descendant
            // cleanup conflicts with the intentionally surviving detached child being asserted.
            var launcherPid = StartInNewProcessGroup(launchCommand.Process.StartInfo);
            var launcherProcessGroupId = GetProcessGroupId(launcherPid);
            Assert.Equal(launcherPid, launcherProcessGroupId);

            var launcherExitCode = await WaitForSpawnedProcessExitAsync(launcherPid);
            AssertRemoteExecutorDidNotFail(launchCommand);
            Assert.Equal(RemoteExecutor.SuccessExitCode, launcherExitCode);

            childPid = await ReadChildPidAsync(childPidFile);
            childProcessGroupId = GetProcessGroupId(childPid.Value);

            // This mirrors an agent/CI command runner cleaning up the process group it created
            // for the completed launcher command; it is not modeling any specific runner.
            SendSignalToProcessGroup(launcherProcessGroupId, SigTerm, ignoreMissingProcessGroup: true);

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.True(IsProcessRunning(childPid.Value));
            Assert.NotEqual(launcherProcessGroupId, childProcessGroupId.Value);
        }
        finally
        {
            if (childProcessGroupId is { } processGroupId)
            {
                SendSignalToProcessGroup(processGroupId, SigKill, ignoreMissingProcessGroup: true);
            }

            if (childPid is { } pid)
            {
                SendSignalToProcess(pid, SigKill, ignoreMissingProcess: true);
                await WaitForProcessExitAsync(pid);
            }

            try
            {
                DisposeRemoteExecutorLaunchCommand(launchCommand);
            }
            finally
            {
                temporaryDirectory.Delete(recursive: true);
            }
        }
    }

    private static int StartInNewProcessGroup(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("The test launcher expects UseShellExecute=false.");
        }

        var arguments = SplitArguments(startInfo.Arguments);
        using var fileName = new NativeUtf8String(startInfo.FileName);
        using var argv = NativeStringArray.Create([startInfo.FileName, .. arguments]);
        using var environment = NativeStringArray.CreateEnvironment(startInfo.Environment);
        using var attributes = new PosixSpawnAttributes();

        attributes.SetNewProcessGroup();

        var spawnResult = posix_spawnp(
            out var processId,
            fileName.Pointer,
            IntPtr.Zero,
            attributes.Pointer,
            argv.Pointer,
            environment.Pointer);
        if (spawnResult != 0)
        {
            throw CreatePosixSpawnException("posix_spawnp", spawnResult);
        }

        return processId;
    }

    private static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '\\' && i + 1 < arguments.Length && arguments[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                AddCurrentArgument(result, current);
            }
            else
            {
                current.Append(c);
            }
        }

        AddCurrentArgument(result, current);
        return result;
    }

    private static void AddCurrentArgument(List<string> result, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        result.Add(current.ToString());
        current.Clear();
    }

    private static async Task<int> ReadChildPidAsync(string childPidFile)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (File.Exists(childPidFile)
                && int.TryParse(await File.ReadAllTextAsync(childPidFile), CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Timed out waiting for child PID file '{childPidFile}'.");
    }

    private static int GetProcessGroupId(int processId)
    {
        var processGroupId = getpgid(processId);
        if (processGroupId < 0)
        {
            throw CreatePosixException("getpgid");
        }

        return processGroupId;
    }

    private static bool IsProcessRunning(int processId)
    {
        if (sys_kill(processId, signal: 0) == 0)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() != Esrch;
    }

    private static void SendSignalToProcessGroup(int processGroupId, int signal, bool ignoreMissingProcessGroup)
    {
        if (sys_kill(-processGroupId, signal) == 0)
        {
            return;
        }

        if (ignoreMissingProcessGroup && Marshal.GetLastPInvokeError() == Esrch)
        {
            return;
        }

        throw CreatePosixException("kill");
    }

    private static void SendSignalToProcess(int processId, int signal, bool ignoreMissingProcess)
    {
        if (sys_kill(processId, signal) == 0)
        {
            return;
        }

        if (ignoreMissingProcess && Marshal.GetLastPInvokeError() == Esrch)
        {
            return;
        }

        throw CreatePosixException("kill");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (!IsProcessRunning(processId))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private static async Task<int> WaitForSpawnedProcessExitAsync(int processId)
    {
        return await Task.Run(() =>
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
                    throw CreatePosixException("waitpid");
                }
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static int GetPosixExitCode(int status)
    {
        if ((status & 0x7f) == 0)
        {
            return (status >> 8) & 0xff;
        }

        return 128 + (status & 0x7f);
    }

    private static void AssertRemoteExecutorDidNotFail(RemoteInvokeHandle launchCommand)
    {
        if (File.Exists(launchCommand.Options.ExceptionFile))
        {
            Assert.Fail(File.ReadAllText(launchCommand.Options.ExceptionFile));
        }
    }

    private static void DisposeRemoteExecutorLaunchCommand(RemoteInvokeHandle? launchCommand)
    {
        if (launchCommand is null)
        {
            return;
        }

        var completedProcess = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"exit {RemoteExecutor.SuccessExitCode}" }
        }) ?? throw new InvalidOperationException("Failed to start completed RemoteExecutor disposal process.");
        completedProcess.WaitForExit();
        launchCommand.Process = completedProcess;
        launchCommand.Dispose();
    }

    private static Win32Exception CreatePosixException(string operation) =>
        new(Marshal.GetLastPInvokeError(), $"Failed to invoke {operation}.");

    private static Win32Exception CreatePosixSpawnException(string operation, int errorCode) =>
        new(errorCode, $"Failed to invoke {operation}.");

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

        public static NativeStringArray CreateEnvironment(IDictionary<string, string?> environment)
        {
            var values = environment
                .Where(static kvp => kvp.Value is not null)
                .Select(static kvp => $"{kvp.Key}={kvp.Value}")
                .ToArray();

            return Create(values);
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

    private const int Esrch = 3;
    private const int Eintr = 4;
    private const int SigKill = 9;
    private const int SigTerm = 15;
    private const short PosixSpawnSetProcessGroup = 0x02;

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc")]
    private static extern int posix_spawnp(
        out int pid,
        IntPtr file,
        IntPtr fileActions,
        IntPtr attrp,
        IntPtr argv,
        IntPtr envp);

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int sys_kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setpgroup(IntPtr attributes, int pgroup);
}
