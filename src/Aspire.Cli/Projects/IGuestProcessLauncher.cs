// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Optional knobs that govern how a guest process is spawned and torn down. Defaults
/// preserve today's behavior for non-Run callers (publish, scaffolding) — they spawn
/// inheriting the parent console and fall back to force-kill on cancellation. The
/// <c>aspire run</c> path passes a populated record so the launcher uses the same
/// new-console + graceful-then-tree-kill ladder that <c>AppHostServerSession</c> uses
/// for the AppHost server child.
/// </summary>
/// <param name="IsolateConsoleForGracefulShutdown">
/// When <see langword="true"/>, spawn the guest via
/// <see cref="IsolatedProcess"/> so it lands in its own hidden console
/// group. Required on Windows for the AttachConsole + GenerateConsoleCtrlEvent dance
/// (executed by <see cref="ProcessTreeGracefulShutdownService"/>) to target the guest
/// without also signalling the CLI itself. No-op on Unix where SIGTERM is sufficient.
/// </param>
/// <param name="GracefulShutdownSignaler">
/// The per-OS "ask this process tree to shut down" primitive (DCP <c>stop-process-tree</c>
/// on Windows, SIGTERM via <c>ProcessSignaler</c> on Unix). When non-<see langword="null"/>
/// (and <paramref name="ShutdownService"/> is non-<see langword="null"/>), the launcher's
/// cancellation path issues this signal before escalating to <c>Process.Kill</c>.
/// </param>
/// <param name="ShutdownService">
/// The central graceful-shutdown budget. Its <see cref="GracefulShutdownService.Token"/>
/// bounds both the graceful-signal call and the post-signal wait-for-exit, so a 2nd Ctrl+C
/// (which calls <see cref="GracefulShutdownService.Expire"/> via the cancellation manager)
/// interrupts both immediately and the ladder escalates to <c>Kill(entireProcessTree: true)</c>.
/// </param>
internal sealed record GuestLaunchOptions(
    bool IsolateConsoleForGracefulShutdown = false,
    IProcessTreeGracefulShutdownSignaler? GracefulShutdownSignaler = null,
    GracefulShutdownService? ShutdownService = null);

/// <summary>
/// Strategy for launching a guest language process.
/// </summary>
internal interface IGuestProcessLauncher
{
    /// <summary>
    /// Launches the guest process with the given command, arguments, and environment.
    /// </summary>
    Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
        string command,
        string[] args,
        DirectoryInfo workingDirectory,
        IDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken,
        Func<Task>? afterLaunchAsync = null,
        GuestLaunchOptions? options = null);
}
