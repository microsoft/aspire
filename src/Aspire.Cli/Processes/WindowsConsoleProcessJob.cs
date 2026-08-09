// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Processes;

/// <summary>
/// Owns a Windows job object that is used as the crash-time safety net for interactive
/// children spawned by <see cref="IsolatedProcess"/>. The job is created
/// once per CLI process — on first isolated spawn via <see cref="Shared"/> — and held for
/// the CLI's entire lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The job is configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so that when the
/// last handle to the job is released — which happens automatically when the parent CLI
/// process exits, even via SIGKILL / power loss — the OS kernel terminates every process
/// currently assigned to the job. This is the only reliable way to prevent orphaned guest
/// AppHosts (which live in their own console group via <c>CREATE_NEW_CONSOLE</c>) from
/// surviving a parent crash on Windows. The Unix equivalent (process-group reparenting
/// to <c>init</c>) provides no equivalent kill behavior, but on Unix the parent's normal
/// SIGINT/SIGTERM signalling reaches the whole process group, so no safety net is needed.
/// </para>
/// <para>
/// The job is also configured with <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> so that DCP (and
/// anything else that needs to outlive the CLI for its own cleanup) can opt out by spawning
/// itself with <c>CREATE_BREAKAWAY_FROM_JOB</c>. Our job only grants permission to break
/// away — the breakaway itself is the responsibility of the spawning code.
/// </para>
/// <para>
/// IMPORTANT: do NOT dispose this service during the normal shutdown ladder. The graceful
/// shutdown path is responsible for cooperatively terminating its children; closing the
/// job handle while they are still alive would convert clean shutdown into a hard kill.
/// Let the OS close the handle on process exit; the kill-on-parent-exit behavior is exactly the
/// crash-safety net we want and is harmless when the children have already exited.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConsoleProcessJob : IDisposable
{
    // The job is a process-wide singleton: its configuration never varies, and the OS closes
    // the handle on process exit (firing KILL_ON_JOB_CLOSE on any still-assigned children).
    // Created lazily on the first isolated spawn that needs it so non-Run invocations — and
    // every non-Windows host — never allocate the kernel object. Threading a job instance
    // through the spawn callers was the alternative; an on-demand singleton removes that
    // plumbing because there is only ever one correct job to use.
    private static readonly Lazy<WindowsConsoleProcessJob> s_shared = new(static () => new WindowsConsoleProcessJob());

    private readonly SafeFileHandle _jobHandle;
    private int _disposed;

    /// <summary>
    /// The process-wide job, created on first access. Callers that opt into parent-lifetime
    /// cleanup use this instead of receiving a job instance, so they cannot forget to supply one.
    /// Intentionally never disposed in production: the OS closes the handle at process exit,
    /// which is exactly the crash-safety net we want.
    /// </summary>
    public static WindowsConsoleProcessJob Shared => s_shared.Value;

    public WindowsConsoleProcessJob()
    {
        _jobHandle = WindowsProcessInterop.CreateJobObjectW(nint.Zero, null);
        if (_jobHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create CLI kill-on-parent-exit job object");
        }

        try
        {
            // BREAKAWAY_OK is required so DCP can fork itself with CREATE_BREAKAWAY_FROM_JOB
            // and survive the CLI exiting; KILL_ON_JOB_CLOSE catches everything else.
            var info = new WindowsProcessInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation =
                {
                    LimitFlags = WindowsProcessInterop.JobObjectLimitKillOnJobClose
                                 | WindowsProcessInterop.JobObjectLimitBreakawayOk,
                },
            };

            var infoSize = Marshal.SizeOf<WindowsProcessInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);

                if (!WindowsProcessInterop.SetInformationJobObject(
                    _jobHandle,
                    WindowsProcessInterop.JobObjectInfoClass.ExtendedLimitInformation,
                    infoPtr,
                    (uint)infoSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure CLI kill-on-parent-exit job object limits");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }
        catch
        {
            // Constructor failure must not leave the partial job alive; closing the handle
            // would also fire KILL_ON_JOB_CLOSE harmlessly (zero processes assigned yet) but
            // mainly we just don't want to leak a kernel object.
            _jobHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The job handle. Pass directly to <see cref="WindowsProcessInterop.SpawnProcess"/> so the spawn
    /// primitive can assign the child to this job atomically at creation via
    /// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>.
    /// </summary>
    public SafeFileHandle Handle => _jobHandle;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Closing the job's last handle fires KILL_ON_JOB_CLOSE on any still-assigned processes.
        // This is intentional as a final safety net for tests and abnormal shutdown — production
        // disposal happens at process exit via the OS, so any process that actually reaches this
        // call is either a test fixture tearing down or a misbehaving consumer; either way killing
        // the stragglers is the correct outcome.
        _jobHandle.Dispose();
    }
}
