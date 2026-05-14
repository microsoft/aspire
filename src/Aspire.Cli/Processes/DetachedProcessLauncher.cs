// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

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
// The .NET 11 Process APIs provide a clean cross-platform solution:
//
//   • StandardOutputHandle / StandardErrorHandle = File.OpenNullHandle()
//     → sends child output to the null device, solving (1).
//
//   • InheritedHandles = [] → restricts handle inheritance to only the
//     standard handles (the NUL handles), preventing accidental leaks to
//     grandchildren, solving (2). Note: InheritedHandles is not supported
//     on macOS, but on Unix only fds 0/1/2 survive exec, so there is no
//     accidental handle leakage to grandchildren anyway.
//

/// <summary>
/// Launches a child process with stdout/stderr suppressed and no handle/fd
/// inheritance to grandchild processes. Used by <c>aspire start</c>.
/// </summary>
internal static class DetachedProcessLauncher
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
    /// <returns>A <see cref="Process"/> object representing the launched child.</returns>
    public static Process Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory, Func<string, bool>? shouldRemoveEnvironmentVariable = null, IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
    {
        using var nullHandle = File.OpenNullHandle();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            // Redirect stdout/stderr to the null device so the child's output
            // never appears on the parent's console.
            StandardOutputHandle = nullHandle,
            StandardErrorHandle = nullHandle,
        };

        // On Windows, restrict handle inheritance to only the standard handles (the NUL
        // handles) so the grandchild (AppHost) doesn't inherit any pipes from the parent.
        // On macOS InheritedHandles is not supported, but on Unix only fds 0/1/2 survive
        // exec so there is no accidental handle leakage to grandchildren anyway.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            startInfo.InheritedHandles = [];
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Remove specified environment variables from the child process.
        // Accessing startInfo.Environment auto-populates from the current process.
        if (shouldRemoveEnvironmentVariable is not null)
        {
            var keysToRemove = new List<string>();
            foreach (var key in startInfo.Environment.Keys)
            {
                if (shouldRemoveEnvironmentVariable(key))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                startInfo.Environment.Remove(key);
            }
        }

        // Add additional environment variables to the child process without mutating the parent.
        if (additionalEnvironmentVariables is not null)
        {
            foreach (var (key, value) in additionalEnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start detached process");
    }
}
