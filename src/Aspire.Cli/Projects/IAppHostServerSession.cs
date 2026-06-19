// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Narrow abstraction over the short-lived AppHost server session used to generate SDK code
/// over RPC.
/// </summary>
/// <remarks>
/// This seam exists only for the code-generation path. That path needs nothing more than
/// "start the server, hand me an RPC client, then dispose", so the interface is intentionally
/// limited to those operations. The run and publish paths construct
/// <see cref="AppHostServerSession"/> directly instead of going through this seam, because they
/// depend on the graceful-shutdown constructor parameters (caller-owned stop token, signaler,
/// shutdown service, Windows console isolation) that have no meaning for a transient codegen
/// session. Tests substitute a fake implementation that returns canned RPC results without
/// launching a real process or opening a socket.
/// </remarks>
internal interface IAppHostServerSession : IAsyncDisposable
{
    /// <summary>
    /// Launches the AppHost server process and wires lifecycle observation. The returned task
    /// completes once the process has been spawned. For codegen the process lifetime is observed
    /// indirectly (via the RPC call and disposal), so this seam exposes no exit-code task.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Connects to the running AppHost server and returns an RPC client for code generation.
    /// </summary>
    Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates the short-lived <see cref="IAppHostServerSession"/> used for SDK code generation.
/// Production wires this to construct a real <see cref="AppHostServerSession"/>; tests inject a
/// factory that returns a fake session.
/// </summary>
internal delegate IAppHostServerSession AppHostServerCodegenSessionFactory(
    IAppHostServerProject project,
    ILogger logger,
    CancellationToken stopRequested,
    ProfilingTelemetry? profilingTelemetry);
