// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Result of preparing an AppHost server for running.
/// </summary>
/// <param name="Success">Whether preparation succeeded.</param>
/// <param name="Output">Build/preparation output for display on failure.</param>
/// <param name="ChannelName">The NuGet channel used (SDK mode only, null for bundle mode).</param>
/// <param name="NeedsCodeGeneration">Whether code generation is needed for the guest language.</param>
internal sealed record AppHostServerPrepareResult(
    bool Success,
    OutputCollector? Output,
    string? ChannelName = null,
    bool NeedsCodeGeneration = false);

/// <summary>
/// Result of <see cref="IAppHostServerProject.RunAsync"/> — a launched AppHost server process plus the
/// captured output.
/// </summary>
/// <param name="SocketPath">RPC socket the server is publishing on.</param>
/// <param name="OutputCollector">Captured stdout/stderr for failure display.</param>
/// <param name="Execution">
/// The started <see cref="IProcessExecution"/> that owns the server child. Callers observe state
/// (<see cref="IProcessExecution.HasExited"/>, <see cref="IProcessExecution.ExitCode"/>,
/// <see cref="IProcessExecution.ProcessId"/>), drive its lifetime via
/// <see cref="IProcessExecution.WaitForExitAsync(CancellationToken)"/> (which runs the shared
/// shutdown ladder on cancellation), and dispose it via
/// <see cref="System.IAsyncDisposable.DisposeAsync"/>. The execution encapsulates the isolated
/// Windows spawn quirk (the underlying Process is obtained via <see cref="System.Diagnostics.Process.GetProcessById(int)"/>),
/// so its status getters are reliable on every path — see https://github.com/dotnet/runtime/issues/45003.
/// </param>
internal sealed record AppHostServerRunResult(
    string SocketPath,
    OutputCollector OutputCollector,
    IProcessExecution Execution);

/// <summary>
/// Controls how <see cref="IAppHostServerProject.RunAsync"/> spawns and tears down the server child.
/// The default (all-null / false) preserves today's force-kill-on-cancel behavior for the non-Run
/// callers (SDK gen, scaffolding, publish, dump). The run path supplies the graceful infrastructure.
/// </summary>
/// <param name="IsolateConsole">
/// When <see langword="true"/>, on Windows the server is spawned via <see cref="IsolatedProcess"/>
/// into its own hidden console (CREATE_NEW_CONSOLE | SW_HIDE) so a graceful shutdown can
/// <c>AttachConsole</c> + post <c>CTRL_C_EVENT</c> against the server without also signalling the CLI,
/// and the child is bound to the process-wide <see cref="WindowsConsoleProcessJob"/> kill-on-close
/// safety net. On Unix the spawn is effectively the same as today's path.
/// </param>
/// <param name="GracefulShutdownSignaler">
/// Issues the graceful shutdown signal during the shared ladder, or <see langword="null"/> to fall
/// back to force-kill on cancellation.
/// </param>
/// <param name="ShutdownService">
/// The command-level graceful window bounding the ladder, or <see langword="null"/> to fall back to
/// force-kill on cancellation.
/// </param>
internal sealed record AppHostServerRunControl(
    bool IsolateConsole = false,
    IProcessTreeGracefulShutdownSignaler? GracefulShutdownSignaler = null,
    IGracefulShutdownWindow? ShutdownService = null);

/// <summary>
/// Represents an AppHost server that can be prepared and run.
/// This abstraction allows for different implementations:
/// - SDK mode: dynamically generates and builds a .NET project
/// - Bundle mode: uses a pre-built server from the Aspire bundle
/// </summary>
internal interface IAppHostServerProject
{
    /// <summary>
    /// Gets the path to the user's app (the polyglot apphost directory).
    /// </summary>
    string AppDirectoryPath { get; }

    /// <summary>
    /// Prepares the AppHost server for running.
    /// For SDK mode: creates project files and builds the project.
    /// For bundle mode: restores integration packages from NuGet.
    /// </summary>
    /// <param name="sdkVersion">The Aspire SDK version to use.</param>
    /// <param name="integrations">The integration references (NuGet packages and/or project references) required by the app host.</param>
    /// <param name="requestedChannel">The package channel to use for this prepare operation, or <see langword="null" /> to use the project configuration.</param>
    /// <param name="packageSourceOverride">Optional package source to prefer for Aspire package restore.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preparation result indicating success/failure and any output.</returns>
    Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the AppHost server process.
    /// </summary>
    /// <remarks>
    /// Implementations should return promptly once the child process has been spawned and its
    /// identity (socket path, PID) is available; long-running initialization should happen after the
    /// returned task completes. <see cref="AppHostServerSession"/> awaits this call while holding its
    /// start gate, and <c>DisposeAsync</c> acquires the same gate — so any latency here directly
    /// delays how quickly a concurrent dispose can begin tearing the session down.
    /// </remarks>
    /// <param name="hostPid">The host process ID (CLI) for orphan detection.</param>
    /// <param name="environmentVariables">Environment variables to pass to the server.</param>
    /// <param name="additionalArgs">Additional command-line arguments.</param>
    /// <param name="debug">Whether to enable debug logging.</param>
    /// <param name="runControl">
    /// Console-isolation + graceful-shutdown wiring for the spawn. <see langword="null"/> (the
    /// default) preserves force-kill-on-cancel semantics for non-Run callers (SDK gen, scaffolding,
    /// publish, dump). The run path passes a populated <see cref="AppHostServerRunControl"/>.
    /// </param>
    /// <returns>A task producing the launched server process execution and its captured output.</returns>
    Task<AppHostServerRunResult> RunAsync(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false,
        AppHostServerRunControl? runControl = null);

    /// <summary>
    /// Gets a unique identifier path for this AppHost, used for running instance detection.
    /// For SDK mode: returns the generated project file path.
    /// For prebuilt mode: returns the app path.
    /// </summary>
    /// <returns>A path that uniquely identifies this AppHost.</returns>
    string GetInstanceIdentifier();
}
