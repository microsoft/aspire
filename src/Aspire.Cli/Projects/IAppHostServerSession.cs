// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Abstraction over an AppHost server child-process session. Two shapes are served through this
/// seam: the short-lived codegen/scaffolding session (start, grab an RPC client, dispose) and the
/// longer-lived <c>aspire run</c> session that additionally exposes the process lifetime
/// (<see cref="WaitForExitAsync"/>), captured output, and the connection details the guest AppHost
/// needs. Tests substitute a fake implementation that returns canned RPC results without launching
/// a real process or opening a socket.
/// </summary>
internal interface IAppHostServerSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the authentication token injected into the server environment. Available before
    /// <see cref="StartAsync"/> so callers can plumb it into the guest AppHost environment.
    /// </summary>
    string AuthenticationToken { get; }

    /// <summary>
    /// Gets the RPC socket path, or <see langword="null"/> if <see cref="StartAsync"/> has not
    /// been called (or threw before the process was published).
    /// </summary>
    string? SocketPath { get; }

    /// <summary>
    /// Gets the output collector for the server's stdout/stderr, or <see langword="null"/> if
    /// <see cref="StartAsync"/> has not been called (or threw before the process was published).
    /// </summary>
    OutputCollector? Output { get; }

    /// <summary>
    /// Gets whether the underlying AppHost server process has exited, or <see langword="null"/>
    /// if <see cref="StartAsync"/> has not been called (or threw before the process was published).
    /// </summary>
    bool? HasServerExited { get; }

    /// <summary>
    /// Reads the AppHost server process's exit code if it has exited, or <see langword="null"/>
    /// if the server is still running, has not been started, or the exit code cannot be read.
    /// </summary>
    int? TryGetServerExitCode();

    /// <summary>
    /// Launches the AppHost server process and wires lifecycle observation. The returned task
    /// completes once the process has been spawned.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Returns the task that completes with the server process exit code. Used by the run path to
    /// observe the server lifetime alongside the guest AppHost.
    /// </summary>
    Task<int> WaitForExitAsync();

    /// <summary>
    /// Connects to the running AppHost server and returns an RPC client for code generation.
    /// </summary>
    Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates <see cref="IAppHostServerSession"/> instances. Production wires this to construct real
/// <see cref="AppHostServerSession"/> instances; tests inject a factory that returns a fake session.
/// </summary>
internal interface IAppHostServerSessionFactory
{
    /// <summary>
    /// Creates an unstarted session for the already-prepared <paramref name="appHostServerProject"/>.
    /// The caller drives it via <see cref="IAppHostServerSession.StartAsync"/>,
    /// <see cref="IAppHostServerSession.GetRpcClientAsync"/>, then disposal. Cancelling
    /// <paramref name="stopRequested"/> (or disposing the session) terminates the server process.
    /// </summary>
    /// <remarks>
    /// The short-lived codegen/scaffolding path passes no graceful-shutdown wiring
    /// (<paramref name="gracefulShutdownSignaler"/> and <paramref name="shutdownService"/> are
    /// <see langword="null"/>, <paramref name="isolateConsole"/> is <see langword="false"/>) because
    /// that session is started, queried over RPC, and disposed within a single operation. The
    /// <c>aspire run</c> path supplies the real signaler/window and isolates the console so a user
    /// Ctrl+C reaches the CLI and drives the shared shutdown ladder rather than the child directly.
    /// </remarks>
    IAppHostServerSession Create(
        IAppHostServerProject appHostServerProject,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        IProcessTreeGracefulShutdownSignaler? gracefulShutdownSignaler,
        IGracefulShutdownWindow? shutdownService,
        bool isolateConsole,
        CancellationToken stopRequested);
}
