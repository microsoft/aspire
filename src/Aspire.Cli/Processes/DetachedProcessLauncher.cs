// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

internal interface IDetachedProcessLauncher
{
    IDetachedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null);
}

internal interface IDetachedProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    DateTime StartTime { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void Kill(bool entireProcessTree);
}

internal sealed class DefaultDetachedProcessLauncher : IDetachedProcessLauncher
{
    public IDetachedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
    {
        return DetachedProcessLauncher.Start(fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables);
    }
}

internal sealed class ProcessBackedDetachedProcess(Process process) : IDetachedProcess
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public DateTime StartTime => process.StartTime;

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => process.WaitForExitAsync(cancellationToken);

    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

    public void Dispose() => process.Dispose();
}

// ============================================================================
// DetachedProcessLauncher — Platform-aware child process launcher for --detach
// ============================================================================
//
// When `aspire start` (or `aspire run --detach`) is used, the CLI spawns a child CLI process which
// in turn spawns the AppHost (the "grandchild"). Two constraints must hold:
//
// 1. The child's stdout/stderr must NOT appear on the parent's console.
//    The parent renders its own summary UX (dashboard URL, PID, log path) and
//    if the child's output (spinners, "Press CTRL+C", etc.) bleeds through, it
//    corrupts the parent's terminal — and breaks E2E tests that pattern-match
//    on the parent's output.
//
// 2. No pipe or handle from the parent→child stdio redirection may leak into
//    the grandchild (AppHost). If it does, callers that wait for the CLI's
//    stdout to close (e.g. Node.js `execSync`, shell `$(...)` substitution)
//    will hang until the AppHost exits — which defeats the purpose of --detach.
//
// These two constraints conflict when using .NET's Process.Start:
//
//   • RedirectStandardOutput = true  → solves (1) but violates (2) on Windows,
//     because .NET calls CreateProcess with bInheritHandles=TRUE, and the pipe
//     write-handle is duplicated into the child. The child passes it to the
//     grandchild (AppHost), keeping the pipe alive.
//
//   • RedirectStandardOutput = false → solves (2) but violates (1), because
//     the child inherits the parent's console and writes directly to it.
//
// The solution is platform-specific:
//
// ┌─────────┬────────────────────────────────────────────────────────────────┐
// │ Windows │ P/Invoke CreateProcess with CREATE_NEW_CONSOLE,               │
// │         │ STARTUPINFOEX, SW_HIDE, and an explicit                       │
// │         │ PROC_THREAD_ATTRIBUTE_HANDLE_LIST. This gives the child an    │
// │         │ independent console lifetime while still letting us set       │
// │         │ bInheritHandles=TRUE (required to assign hStdOutput to NUL)   │
// │         │ and restrict inheritance to ONLY the NUL handle — so the      │
// │         │ grandchild inherits nothing useful. Child stdout/stderr go to │
// │         │ the NUL device.                                               │
// │         │                                                               │
// │ Linux / │ posix_spawn /bin/sh with POSIX_SPAWN_SETPGROUP, stdio bound  │
// │ macOS   │ to /dev/null, and a small exec handoff. The new process group │
// │         │ keeps the child CLI and AppHost out of launcher process-group │
// │         │ cleanup after `aspire start` returns, while /dev/null stdio   │
// │         │ prevents the detached child from corrupting parent output or  │
// │         │ keeping caller-observed pipes alive.                          │
// └─────────┴────────────────────────────────────────────────────────────────┘
//

/// <summary>
/// Launches a child process with stdout/stderr suppressed and no handle/fd
/// inheritance to grandchild processes. Used by <c>aspire start</c>.
/// </summary>
internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Starts a detached child process with stdout/stderr going to the null device
    /// and no inheritable handles/fds leaking to grandchildren.
    /// </summary>
    /// <param name="fileName">The executable path (e.g. dotnet or the native CLI).</param>
    /// <param name="arguments">The command-line arguments for the child process.</param>
    /// <param name="workingDirectory">The working directory for the child process.</param>
    /// <param name="shouldRemoveEnvironmentVariable">Optional predicate that returns <see langword="true" /> for environment variable names that should be removed from the child process.</param>
    /// <param name="additionalEnvironmentVariables">Optional dictionary of environment variables to add to the child process without mutating the parent.</param>
    /// <returns>An <see cref="IDetachedProcess"/> object representing the launched child.</returns>
    public static IDetachedProcess Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory, Func<string, bool>? shouldRemoveEnvironmentVariable = null, IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return StartWindows(fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables);
        }

        return StartUnix(fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables);
    }
}
