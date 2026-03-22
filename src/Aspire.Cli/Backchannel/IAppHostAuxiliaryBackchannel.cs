// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Represents a connection to an AppHost instance via the auxiliary backchannel.
/// </summary>
internal interface IAppHostAuxiliaryBackchannel : IDisposable
{
    /// <summary>
    /// Gets the hash identifier for this AppHost instance.
    /// </summary>
    string Hash { get; }

    /// <summary>
    /// Gets the socket path for this connection.
    /// </summary>
    string SocketPath { get; }

    /// <summary>
    /// Gets the MCP connection information for the Dashboard.
    /// </summary>
    DashboardMcpConnectionInfo? McpInfo { get; }

    /// <summary>
    /// Gets the AppHost information.
    /// </summary>
    AppHostInformation? AppHostInfo { get; }

    /// <summary>
    /// Gets a value indicating whether this AppHost is within the scope of the MCP server's working directory.
    /// </summary>
    bool IsInScope { get; }

    /// <summary>
    /// Gets the timestamp when this connection was established.
    /// </summary>
    DateTimeOffset ConnectedAt { get; }

    /// <summary>
    /// Gets a value indicating whether the AppHost supports v2 API.
    /// </summary>
    bool SupportsV2 { get; }

    /// <summary>
    /// Gets the Dashboard URLs from the AppHost.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Dashboard URLs state including health and login URLs.</returns>
    Task<DashboardUrlsState?> GetDashboardUrlsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current resource snapshots from the AppHost.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of resource snapshots representing current state.</returns>
    Task<List<ResourceSnapshot>> GetResourceSnapshotsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Watches for resource snapshot changes and streams them from the AppHost.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of resource snapshots as they change.</returns>
    IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets resource log lines from the AppHost.
    /// </summary>
    /// <param name="resourceName">Optional resource name. If null, streams logs from all resources (only valid when follow is true).</param>
    /// <param name="follow">If true, continuously streams new logs. If false, returns existing logs and completes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of log lines.</returns>
    IAsyncEnumerable<ResourceLogLine> GetResourceLogsAsync(
        string? resourceName = null,
        bool follow = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the AppHost by sending a stop request via the backchannel.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the stop request was sent successfully, false otherwise.</returns>
    Task<bool> StopAppHostAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls an MCP tool on a resource via the AppHost backchannel.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="toolName">The name of the tool to call.</param>
    /// <param name="arguments">Optional arguments to pass to the tool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the tool call.</returns>
    Task<CallToolResult> CallResourceMcpToolAsync(
        string resourceName,
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets Dashboard information using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Dashboard information response.</returns>
    Task<GetDashboardInfoResponse?> GetDashboardInfoV2Async(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command on a resource.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="commandName">The name of the command (e.g., "start", "stop", "restart").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the command execution.</returns>
    Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(
        string resourceName,
        string commandName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for a resource to reach a target status on the AppHost side.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="status">The target status ("up", "healthy", "down").</param>
    /// <param name="timeoutSeconds">The timeout in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the wait operation.</returns>
    Task<WaitForResourceResponse> WaitForResourceAsync(
        string resourceName,
        string status,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}
