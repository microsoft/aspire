// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

// Global namespace (no wrapper) so both Aspire.Cli.Tests and Aspire.Hosting.Tests can link this
// shared helper without a project-specific using, matching the sibling TempDirectory.cs.
internal static class TestProcesses
{
    // A leaked long-running child lingers on a CI agent until the box is recycled. The process-lifecycle
    // tests kill it explicitly within seconds, but if a test aborts before its `using` disposes (or the
    // test host is SIGKILLed for a hangdump), the child can be orphaned. Two backstops guard against that:
    //
    //  1. Every started process is tracked and reaped on a graceful test-host shutdown (ProcessExit).
    //  2. The process self-terminates after a generous ceiling, so even an ungraceful SIGKILL (where
    //     ProcessExit never runs) cannot leave it running forever.
    //
    // The ceiling only matters when normal cleanup was skipped; healthy tests never come close to it.
    private static readonly TimeSpan s_maxLifetime = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<int, Process> s_tracked = new();
    private static int s_exitHookInstalled;

    /// <summary>
    /// Starts a cross-platform, long-running child process that stands in for a launcher/parent process
    /// in liveness tests. The caller owns the returned process and should kill and dispose it; the helper
    /// tracks it and reaps it on a graceful test-host shutdown, and the process self-terminates after a
    /// bounded lifetime as a last-resort backstop against leaks.
    /// </summary>
    public static Process StartLongRunning()
    {
        EnsureExitHookInstalled();

        // Bounded stand-in for the previous indefinite `ping -t` / `tail -f /dev/null`: a trivially
        // available command that blocks for a fixed, generous duration and then exits on its own.
        //   Windows: `ping -n <seconds> 127.0.0.1` sends one echo per ~second, so the count ≈ seconds.
        //   Unix:    `sleep <seconds>`.
        var seconds = ((int)s_maxLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", $"-n {seconds} 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", seconds) { CreateNoWindow = true };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start long-running test process.");
        s_tracked[process.Id] = process;
        return process;
    }

    private static void EnsureExitHookInstalled()
    {
        if (Interlocked.Exchange(ref s_exitHookInstalled, 1) != 0)
        {
            return;
        }

        // Best-effort reaping when the test host shuts down cleanly. ProcessExit does not run on SIGKILL,
        // which is why StartLongRunning also caps the child's lifetime.
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            foreach (var process in s_tracked.Values)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The process may already be gone or disposed by its owning test; nothing to do.
                }
            }
        };
    }
}
