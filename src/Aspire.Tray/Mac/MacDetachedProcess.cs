// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Tray;

[SupportedOSPlatform("macos")]
internal sealed partial class MacDetachedProcess
{
    private int _processId;

    private MacDetachedProcess(int processId) => _processId = processId;

    internal int ProcessId => _processId;

    internal async Task CompleteStartupAsync(Func<Task> ready)
    {
        try
        {
            await ready().ConfigureAwait(false);
        }
        catch (Exception startupError)
        {
            try
            {
                await TerminateAndWaitAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Tray startup failed and the child could not be cleaned up.", startupError, cleanupError);
            }
            throw;
        }
        Detach();
    }

    public static MacDetachedProcess Start(ProcessStartInfo startInfo, SafeFileHandle log)
    {
        Check(InitializeActions(out var actions), "initialize tray file actions");
        try
        {
            Check(InitializeAttributes(out var attributes), "initialize tray launch attributes");
            try
            {
                // SETSID disconnects the GUI from the terminal's process group/session.
                // CLOEXEC_DEFAULT permits only explicitly duplicated stdio descriptors.
                // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/spawn.h
                Check(SetFlags(ref attributes, 0x0400 | 0x4000 | 0x0008), "detach the tray");
                uint signalMask = 0;
                Check(SetSignalMask(ref attributes, ref signalMask), "reset the tray signal mask");
                var addedReference = false;
                try
                {
                    log.DangerousAddRef(ref addedReference);
                    var descriptor = log.DangerousGetHandle().ToInt32();
                    Check(AddDuplicate(ref actions, descriptor, 1), "redirect tray output");
                    Check(AddDuplicate(ref actions, descriptor, 2), "redirect tray errors");
                    // If the caller closed stdin, opening the log may have reused fd 0.
                    // Copy it to stdout/stderr before replacing the child's stdin.
                    Check(AddOpen(ref actions, 0, "/dev/null", 0, 0), "redirect tray input");
                    Check(AddWorkingDirectory(ref actions, startInfo.WorkingDirectory), "set the tray working directory");
                    using var arguments = new NativeStrings([startInfo.FileName, .. startInfo.ArgumentList]);
                    using var environment = new NativeStrings(startInfo.Environment.Select(pair => $"{pair.Key}={pair.Value}"));
                    Check(Spawn(out var processId, startInfo.FileName, ref actions, ref attributes,
                        arguments.Values, environment.Values), "launch the tray");
                    return new(processId);
                }
                finally
                {
                    if (addedReference)
                    {
                        log.DangerousRelease();
                    }
                }
            }
            finally
            {
                Check(DestroyAttributes(ref attributes), "release tray launch attributes");
            }
        }
        finally
        {
            Check(DestroyActions(ref actions), "release tray file actions");
        }
    }

    public async Task TerminateAndWaitAsync()
    {
        if (_processId == 0)
        {
            return;
        }
        // This child is deliberately not reaped before kill. Even if it exits during
        // startup, its PID cannot be reused for another process until our waitpid.
        if (Kill(_processId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not terminate the failed tray launch.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The failed tray launch did not exit after termination.");
        }
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (_processId != 0)
        {
            var result = WaitPid(_processId, out _, 1);
            if (result == _processId)
            {
                _processId = 0;
                return;
            }
            if (result < 0 && Marshal.GetLastPInvokeError() != 4)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not wait for the tray launch to exit.");
            }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Detach()
    {
        // The launcher normally exits immediately; if hosted longer, reap the child
        // without keeping the process alive or retaining any inherited output pipe.
        _ = ReapAsync();
    }

    private async Task ReapAsync()
    {
        try
        {
            await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"Could not reap the detached tray process: {ex.Message}");
        }
    }

    private static void Check(int error, string operation)
    {
        if (error != 0)
        {
            throw new Win32Exception(error, $"Could not {operation}.");
        }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Spawn(out int processId, string path, ref nint actions, ref nint attributes,
        nint[] arguments, nint[] environment);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_init")]
    private static partial int InitializeActions(out nint actions);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static partial int DestroyActions(ref nint actions);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_init")]
    private static partial int InitializeAttributes(out nint attributes);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_destroy")]
    private static partial int DestroyAttributes(ref nint attributes);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_setflags")]
    private static partial int SetFlags(ref nint attributes, short flags);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_setsigmask")]
    private static partial int SetSignalMask(ref nint attributes, ref uint mask);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static partial int AddDuplicate(ref nint actions, int descriptor, int destination);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AddOpen(ref nint actions, int descriptor, string path, int flags, ushort mode);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AddWorkingDirectory(ref nint actions, string path);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int WaitPid(int processId, out int status, int options);

    private sealed class NativeStrings : IDisposable
    {
        public nint[] Values { get; }

        public NativeStrings(IEnumerable<string> values)
        {
            var strings = values.ToArray();
            Values = new nint[strings.Length + 1];
            try
            {
                for (var index = 0; index < strings.Length; index++)
                {
                    Values[index] = Marshal.StringToCoTaskMemUTF8(strings[index]);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var value in Values)
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
    }
}
