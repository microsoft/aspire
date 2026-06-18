// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Processes;

/// <summary>
/// Minimal abstraction over the per-OS "ask this process tree to shut down gracefully" primitive
/// used by the in-process <c>aspire run</c> shutdown ladders (AppHost server + guest siblings).
/// On Windows it shells out to DCP's <c>stop-process-tree</c> (the AttachConsole +
/// GenerateConsoleCtrlEvent dance); on Unix it sends SIGTERM via <c>ProcessSignaler</c>.
/// Callers own the wait/escalate cadence; this method does not wait for exit or force-kill.
/// </summary>
/// <remarks>
/// Existence as an interface (rather than a direct dependency on
/// <see cref="ProcessTreeGracefulShutdownService"/>) keeps the in-process Run shutdown ladders
/// testable: tests can inject a fake that simulates "signal failed," "signal returned false,"
/// or "signal was issued and observed by a fake process" without needing real DCP layout
/// discovery or platform-specific signal plumbing.
/// </remarks>
internal interface IProcessTreeGracefulShutdownSignaler
{
    Task<bool> RequestProcessTreeGracefulShutdownAsync(
        int pid,
        DateTimeOffset? startTime,
        bool includeStartTimeForDcp,
        CancellationToken cancellationToken);
}
