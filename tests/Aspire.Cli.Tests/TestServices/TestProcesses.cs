// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Tests.TestServices;

internal static class TestProcesses
{
    /// <summary>
    /// Starts a cross-platform, long-running child process that stands in for a launcher/parent process
    /// in liveness tests. The caller owns the returned process and must kill and dispose it.
    /// </summary>
    public static Process StartLongRunning()
    {
        // Mirrors CliOrphanDetectorTests' cross-platform process choice for CI reliability: a trivially
        // available command that blocks indefinitely until it is killed.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t localhost") { CreateNoWindow = true }
            : new ProcessStartInfo("tail", "-f /dev/null") { CreateNoWindow = true };

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start long-running test process.");
    }
}
