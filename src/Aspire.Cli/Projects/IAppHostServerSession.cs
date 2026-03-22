// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Utils;

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Projects;

/// <summary>
/// Represents a running AppHost server session.
/// Manages server lifecycle and provides RPC client access.
/// </summary>
internal interface IAppHostServerSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the socket path for RPC communication.
    /// </summary>
    string SocketPath { get; }

    /// <summary>
    /// Gets the server process.
    /// </summary>
    Process ServerProcess { get; }

    /// <summary>
    /// Gets the output collector for server stdout/stderr.
    /// </summary>
    OutputCollector Output { get; }

    /// <summary>
    /// Gets an RPC client connected to this session.
    /// </summary>
    Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Factory for building and starting AppHost server sessions.
/// </summary>
internal interface IAppHostServerSessionFactory
{
    /// <summary>
    /// Builds and starts an AppHost server session.
    /// </summary>
    /// <param name="appHostPath">The path to the AppHost project directory.</param>
    /// <param name="sdkVersion">The Aspire SDK version to use.</param>
    /// <param name="integrations">The integration references to include.</param>
    /// <param name="launchSettingsEnvVars">Optional environment variables from launch settings.</param>
    /// <param name="debug">Whether to enable debug logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result containing the session if successful.</returns>
    Task<AppHostServerSessionResult> CreateAsync(
        string appHostPath,
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        Dictionary<string, string>? launchSettingsEnvVars,
        bool debug,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of creating an AppHost server session.
/// </summary>
/// <param name="Success">Whether the build was successful.</param>
/// <param name="Session">The session if successful, null otherwise.</param>
/// <param name="BuildOutput">The build output for error diagnostics, may be null.</param>
/// <param name="ChannelName">The NuGet channel name used, if any.</param>
internal record AppHostServerSessionResult(
    bool Success,
    IAppHostServerSession? Session,
    OutputCollector? BuildOutput,
    string? ChannelName);
