// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Commands;

/// <summary>
/// Tuning for the launcher watchdog that <see cref="BaseCommand"/> arms around commands which opt in via
/// <see cref="BaseCommand.RunsUnderLauncherWatchdog"/>.
/// </summary>
internal sealed class LauncherWatchdogOptions
{
    /// <summary>
    /// The interval between parent-liveness probes. <see langword="null"/> uses the monitor's production
    /// default of one second.
    /// </summary>
    /// <remarks>
    /// Only tests set this. A command that is killed mid-stream is detected on the next probe, so a test
    /// that waits for that detection would otherwise race wall-clock scheduling and time out under CPU
    /// contention. Shortening the interval keeps the assertion about the watchdog firing rather than about
    /// how quickly the machine happened to schedule the poll.
    /// </remarks>
    public TimeSpan? PollInterval { get; init; }
}
