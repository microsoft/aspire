// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using StreamJsonRpc;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Represents a connection to an AppHost instance via the auxiliary backchannel.
/// Encapsulates connection management and RPC method calls.
/// </summary>
internal sealed class AppHostAuxiliaryBackchannel : IAppHostAuxiliaryBackchannel
{
    private readonly ILogger? _logger;
    private JsonRpc? _rpc;
    private bool _disposed;
    private readonly ImmutableHashSet<string> _capabilities;

    /// <summary>
    /// Private constructor - use factory methods to create instances.
    /// </summary>
    private AppHostAuxiliaryBackchannel(
        string hash,
        string socketPath,
        JsonRpc rpc,
        DashboardMcpConnectionInfo? mcpInfo,
        AppHostInformation? appHostInfo,
        bool isInScope,
        ImmutableHashSet<string> capabilities,
        ILogger? logger)
    {
        Hash = hash;
        SocketPath = socketPath;
        _rpc = rpc;
        McpInfo = mcpInfo;
        AppHostInfo = appHostInfo;
        IsInScope = isInScope;
        _capabilities = capabilities;
        ConnectedAt = DateTimeOffset.UtcNow;
        _logger = logger;
    }

    /// <summary>
    /// Internal constructor for testing purposes.
    /// </summary>
    internal AppHostAuxiliaryBackchannel(
        string hash,
        string socketPath,
        JsonRpc rpc,
        DashboardMcpConnectionInfo? mcpInfo,
        AppHostInformation? appHostInfo,
        bool isInScope)
        : this(hash, socketPath, rpc, mcpInfo, appHostInfo, isInScope, ImmutableHashSet<string>.Empty, null)
    {
    }

    /// <inheritdoc />
    public string Hash { get; private set; }

    /// <inheritdoc />
    public string SocketPath { get; }

    /// <inheritdoc />
    public DashboardMcpConnectionInfo? McpInfo { get; private set; }

    /// <inheritdoc />
    public AppHostInformation? AppHostInfo { get; private set; }

    /// <inheritdoc />
    public bool IsInScope { get; internal set; }

    /// <inheritdoc />
    public DateTimeOffset ConnectedAt { get; }

    /// <inheritdoc />
    public bool SupportsV2 => _capabilities.Contains(AuxiliaryBackchannelCapabilities.V2);

    /// <summary>
    /// Gets the JSON-RPC proxy for communicating with the AppHost.
    /// </summary>
    internal JsonRpc? Rpc => _rpc;

    /// <summary>
    /// Ensures the connection is valid and returns the RPC proxy.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the object has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if not connected to the backchannel.</exception>
    private JsonRpc EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rpc is null)
        {
            throw new InvalidOperationException("Not connected to auxiliary backchannel.");
        }
        return _rpc;
    }

    /// <summary>
    /// Creates and connects a new auxiliary backchannel to the specified socket path.
    /// </summary>
    /// <param name="socketPath">The path to the Unix domain socket.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected AppHostAuxiliaryBackchannel instance.</returns>
    public static Task<AppHostAuxiliaryBackchannel> ConnectAsync(
        string socketPath,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath) ?? string.Empty;
        return CreateFromSocketAsync(hash, socketPath, isInScope: true, socket: null, logger, cancellationToken);
    }

    /// <summary>
    /// Creates an AppHostAuxiliaryBackchannel by connecting to the specified socket path,
    /// or using an already-connected socket if provided.
    /// This is the single path for all connection creation, ensuring capabilities are always fetched.
    /// </summary>
    /// <param name="hash">The AppHost hash identifier.</param>
    /// <param name="socketPath">The socket path.</param>
    /// <param name="isInScope">Whether this AppHost is within the scope of the working directory.</param>
    /// <param name="socket">Optional already-connected socket. If null, a new connection will be established.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token (only used when socket is null).</param>
    /// <returns>A connected AppHostAuxiliaryBackchannel instance.</returns>
    internal static async Task<AppHostAuxiliaryBackchannel> CreateFromSocketAsync(
        string hash,
        string socketPath,
        bool isInScope,
        Socket? socket = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        // Connect if no socket provided
        if (socket is null)
        {
            logger?.LogDebug("Connecting to auxiliary backchannel at {SocketPath}", socketPath);

            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        // Create JSON-RPC connection with proper formatter
        var stream = new NetworkStream(socket, ownsSocket: true);
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter()));
        rpc.StartListening();

        logger?.LogDebug("Connected to auxiliary backchannel at {SocketPath}", socketPath);

        // Fetch all connection info
        var appHostInfo = await rpc.InvokeAsync<AppHostInformation?>("GetAppHostInformationAsync").ConfigureAwait(false);
        var mcpInfo = await rpc.InvokeAsync<DashboardMcpConnectionInfo?>("GetDashboardMcpConnectionInfoAsync").ConfigureAwait(false);
        var capabilities = await FetchCapabilitiesAsync(rpc, logger).ConfigureAwait(false);

        var capabilitiesSet = capabilities?.ToImmutableHashSet() ?? ImmutableHashSet.Create(AuxiliaryBackchannelCapabilities.V1);

        return new AppHostAuxiliaryBackchannel(hash, socketPath, rpc, mcpInfo, appHostInfo, isInScope, capabilitiesSet, logger);
    }

    /// <summary>
    /// Fetches capabilities from an AppHost via RPC.
    /// </summary>
    /// <param name="rpc">The JSON-RPC connection.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The capabilities array, or null if not supported.</returns>
    private static async Task<string[]?> FetchCapabilitiesAsync(JsonRpc rpc, ILogger? logger = null)
    {
        try
        {
            var response = await rpc.InvokeAsync<GetCapabilitiesResponse?>("GetCapabilitiesAsync", [null]).ConfigureAwait(false);
            var capabilities = response?.Capabilities;
            logger?.LogDebug("AppHost capabilities: {Capabilities}", capabilities is not null ? string.Join(", ", capabilities) : "null");
            return capabilities;
        }
        catch (RemoteMethodNotFoundException)
        {
            // Older AppHost without GetCapabilitiesAsync - assume v1 only
            logger?.LogDebug("AppHost does not support GetCapabilitiesAsync, assuming v1 only");
            return null;
        }
        catch (Exception ex)
        {
            // Log any other exception
            logger?.LogWarning(ex, "Failed to fetch capabilities from AppHost");
            return null;
        }
    }

    /// <summary>
    /// Gets the AppHost information including process IDs and path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AppHost information, or null if unavailable.</returns>
    public async Task<AppHostInformation?> GetAppHostInformationAsync(CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Requesting AppHost information");

        var appHostInfo = await rpc.InvokeWithCancellationAsync<AppHostInformation?>(
            "GetAppHostInformationAsync",
            [],
            cancellationToken).ConfigureAwait(false);

        return appHostInfo;
    }

    /// <inheritdoc />
    public async Task<bool> StopAppHostAsync(CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Requesting AppHost to stop");

        try
        {
            await rpc.InvokeWithCancellationAsync(
                "StopAppHostAsync",
                [],
                cancellationToken).ConfigureAwait(false);

            _logger?.LogDebug("Stop request sent to AppHost");
            return true;
        }
        catch (RemoteMethodNotFoundException ex)
        {
            // The RPC method may not be available on older AppHost versions.
            _logger?.LogDebug(ex, "StopAppHostAsync RPC method not available on the remote AppHost. The AppHost may be running an older version.");
            return false;
        }
    }

    /// <summary>
    /// Gets the Dashboard MCP connection information.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The MCP connection information, or null if unavailable.</returns>
    public async Task<DashboardMcpConnectionInfo?> GetDashboardMcpConnectionInfoAsync(CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Requesting Dashboard MCP connection info");

        var mcpInfo = await rpc.InvokeWithCancellationAsync<DashboardMcpConnectionInfo?>(
            "GetDashboardMcpConnectionInfoAsync",
            [],
            cancellationToken).ConfigureAwait(false);

        return mcpInfo;
    }

    /// <inheritdoc />
    public async Task<DashboardUrlsState?> GetDashboardUrlsAsync(CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Requesting Dashboard URLs");

        try
        {
            var dashboardUrls = await rpc.InvokeWithCancellationAsync<DashboardUrlsState?>(
                "GetDashboardUrlsAsync",
                [],
                cancellationToken).ConfigureAwait(false);

            return dashboardUrls;
        }
        catch (RemoteMethodNotFoundException ex)
        {
            // The RPC method may not be available on older AppHost versions.
            _logger?.LogDebug(ex, "GetDashboardUrlsAsync RPC method not available on the remote AppHost. The AppHost may be running an older version.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<ResourceSnapshot>> GetResourceSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Getting resource snapshots");

        try
        {
            var snapshots = await rpc.InvokeWithCancellationAsync<List<ResourceSnapshot>>(
                "GetResourceSnapshotsAsync",
                [],
                cancellationToken).ConfigureAwait(false) ?? [];

            // Sort resources by name for consistent ordering.
            snapshots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            return snapshots;
        }
        catch (RemoteMethodNotFoundException ex)
        {
            _logger?.LogDebug(ex, "GetResourceSnapshotsAsync RPC method not available on the remote AppHost. The AppHost may be running an older version.");
            return [];
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Starting resource snapshots watch");

        IAsyncEnumerable<ResourceSnapshot>? snapshots;
        try
        {
            snapshots = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<ResourceSnapshot>>(
                "WatchResourceSnapshotsAsync",
                [],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteMethodNotFoundException ex)
        {
            _logger?.LogDebug(ex, "WatchResourceSnapshotsAsync RPC method not available on the remote AppHost. The AppHost may be running an older version.");
            yield break;
        }

        if (snapshots is null)
        {
            yield break;
        }

        await foreach (var snapshot in snapshots.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResourceLogLine> GetResourceLogsAsync(
        string? resourceName = null,
        bool follow = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Getting resource logs for {ResourceName} (follow={Follow})", resourceName ?? "all resources", follow);

        IAsyncEnumerable<ResourceLogLine>? logLines;
        try
        {
            logLines = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<ResourceLogLine>>(
                "GetResourceLogsAsync",
                [resourceName, follow],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteMethodNotFoundException ex)
        {
            _logger?.LogDebug(ex, "GetResourceLogsAsync RPC method not available on the remote AppHost. The AppHost may be running an older version.");
            yield break;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Error calling GetResourceLogsAsync RPC method. The AppHost may be running an incompatible version.");
            yield break;
        }

        if (logLines is null)
        {
            yield break;
        }

        await foreach (var logLine in logLines.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return logLine;
        }
    }

    /// <inheritdoc />
    public async Task<CallToolResult> CallResourceMcpToolAsync(
        string resourceName,
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Requesting AppHost to call MCP tool {ToolName} on resource {ResourceName}", toolName, resourceName);

        return await rpc.InvokeWithCancellationAsync<CallToolResult>(
            "CallResourceMcpToolAsync",
            [resourceName, toolName, arguments],
            cancellationToken).ConfigureAwait(false);
    }

    #region V2 API Methods

    /// <summary>
    /// Gets AppHost information using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AppHost information response.</returns>
    public async Task<GetAppHostInfoResponse?> GetAppHostInfoV2Async(CancellationToken cancellationToken = default)
    {
        if (!SupportsV2)
        {
            // Fall back to v1 and convert
            var legacyInfo = await GetAppHostInformationAsync(cancellationToken).ConfigureAwait(false);
            if (legacyInfo is null)
            {
                return null;
            }

            return new GetAppHostInfoResponse
            {
                Pid = legacyInfo.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AspireHostVersion = "unknown",
                AppHostPath = legacyInfo.AppHostPath,
                CliProcessId = legacyInfo.CliProcessId,
                StartedAt = legacyInfo.StartedAt
            };
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Getting AppHost info (v2)");

        return await rpc.InvokeWithCancellationAsync<GetAppHostInfoResponse>(
            "GetAppHostInfoAsync",
            [null],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets Dashboard information using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Dashboard information response.</returns>
    public async Task<GetDashboardInfoResponse?> GetDashboardInfoV2Async(CancellationToken cancellationToken = default)
    {
        if (!SupportsV2)
        {
            // Fall back to v1 - ApiBaseUrl and ApiToken are only available in v2
            var mcpInfo = await GetDashboardMcpConnectionInfoAsync(cancellationToken).ConfigureAwait(false);
            var urlsState = await GetDashboardUrlsAsync(cancellationToken).ConfigureAwait(false);

            var urls = new List<string>();
            if (!string.IsNullOrEmpty(urlsState?.BaseUrlWithLoginToken))
            {
                urls.Add(urlsState.BaseUrlWithLoginToken);
            }
            if (!string.IsNullOrEmpty(urlsState?.CodespacesUrlWithLoginToken))
            {
                urls.Add(urlsState.CodespacesUrlWithLoginToken);
            }

            return new GetDashboardInfoResponse
            {
                McpBaseUrl = mcpInfo?.EndpointUrl,
                McpApiToken = mcpInfo?.ApiToken,
                ApiBaseUrl = null, // Not available in v1
                ApiToken = null,   // Not available in v1
                DashboardUrls = urls.ToArray(),
                IsHealthy = urlsState?.DashboardHealthy ?? false
            };
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Getting Dashboard info (v2)");

        return await rpc.InvokeWithCancellationAsync<GetDashboardInfoResponse>(
            "GetDashboardInfoAsync",
            [null],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets resource snapshots using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="request">The request with optional filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resources response.</returns>
    public async Task<GetResourcesResponse> GetResourcesV2Async(GetResourcesRequest? request = null, CancellationToken cancellationToken = default)
    {
        if (!SupportsV2)
        {
            // Fall back to v1
            var snapshots = await GetResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false);

            // Apply filter if specified
            if (!string.IsNullOrEmpty(request?.Filter))
            {
                var filter = request.Filter;
                snapshots = snapshots.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return new GetResourcesResponse
            {
                Resources = snapshots.ToArray()
            };
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Getting resources (v2)");

        return await rpc.InvokeWithCancellationAsync<GetResourcesResponse>(
            "GetResourcesAsync",
            [request],
            cancellationToken).ConfigureAwait(false) ?? new GetResourcesResponse { Resources = [] };
    }

    /// <summary>
    /// Watches for resource changes using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="request">The request with optional filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of resource snapshots.</returns>
    public async IAsyncEnumerable<ResourceSnapshot> WatchResourcesV2Async(
        WatchResourcesRequest? request = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!SupportsV2)
        {
            // Fall back to v1
            var filter = request?.Filter;
            await foreach (var snapshot in WatchResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(filter) && !snapshot.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return snapshot;
            }
            yield break;
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Watching resources (v2)");

        IAsyncEnumerable<ResourceSnapshot>? snapshots;
        try
        {
            snapshots = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<ResourceSnapshot>>(
                "WatchResourcesAsync",
                [request],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteMethodNotFoundException)
        {
            yield break;
        }

        if (snapshots is null)
        {
            yield break;
        }

        await foreach (var snapshot in snapshots.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    /// <summary>
    /// Gets console logs using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="request">The request specifying resource and options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of log lines.</returns>
    public IAsyncEnumerable<ResourceLogLine> GetConsoleLogsV2Async(
        GetConsoleLogsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SupportsV2)
        {
            // Fall back to v1
            return GetResourceLogsAsync(request.ResourceName, request.Follow, cancellationToken);
        }

        return GetConsoleLogsV2InternalAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ResourceLogLine> GetConsoleLogsV2InternalAsync(
        GetConsoleLogsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Getting console logs (v2) for {ResourceName}", request.ResourceName);

        IAsyncEnumerable<ResourceLogLine>? logLines;
        try
        {
            logLines = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<ResourceLogLine>>(
                "GetConsoleLogsAsync",
                [request],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteMethodNotFoundException)
        {
            yield break;
        }

        if (logLines is null)
        {
            yield break;
        }

        await foreach (var logLine in logLines.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return logLine;
        }
    }

    /// <summary>
    /// Calls an MCP tool using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="request">The request specifying resource, tool, and arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tool call response.</returns>
    public async Task<CallMcpToolResponse> CallMcpToolV2Async(
        CallMcpToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SupportsV2)
        {
            // Fall back to v1 - convert request arguments
            Dictionary<string, JsonElement>? arguments = null;
            if (request.Arguments is JsonElement argsElement && argsElement.ValueKind == JsonValueKind.Object)
            {
                arguments = new Dictionary<string, JsonElement>();
                foreach (var prop in argsElement.EnumerateObject())
                {
                    arguments[prop.Name] = prop.Value;
                }
            }

            var result = await CallResourceMcpToolAsync(request.ResourceName, request.ToolName, arguments, cancellationToken).ConfigureAwait(false);

            return new CallMcpToolResponse
            {
                IsError = result.IsError ?? false,
                Content = result.Content.Select(c => new McpToolContentItem
                {
                    Type = c.Type,
                    Text = (c as ModelContextProtocol.Protocol.TextContentBlock)?.Text
                }).ToArray()
            };
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Calling MCP tool (v2) {ToolName} on {ResourceName}", request.ToolName, request.ResourceName);

        return await rpc.InvokeWithCancellationAsync<CallMcpToolResponse>(
            "CallMcpToolAsync",
            [request],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the AppHost using the v2 API.
    /// Falls back to v1 if not supported.
    /// </summary>
    /// <param name="request">The request with optional exit code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the stop was initiated, false if the method wasn't available.</returns>
    public async Task<bool> StopAppHostV2Async(StopAppHostRequest? request = null, CancellationToken cancellationToken = default)
    {
        if (!SupportsV2)
        {
            // Fall back to v1
            return await StopAppHostAsync(cancellationToken).ConfigureAwait(false);
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Stopping AppHost (v2)");

        try
        {
            await rpc.InvokeWithCancellationAsync<StopAppHostResponse>(
                "StopAsync",
                [request],
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RemoteMethodNotFoundException)
        {
            // Fall back to v1
            return await StopAppHostAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a command on a resource.
    /// </summary>
    public async Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(
        string resourceName,
        string commandName,
        CancellationToken cancellationToken = default)
    {
        var rpc = EnsureConnected();

        _logger?.LogDebug("Executing command '{CommandName}' on resource '{ResourceName}'", commandName, resourceName);

        var request = new ExecuteResourceCommandRequest
        {
            ResourceName = resourceName,
            CommandName = commandName
        };

        var response = await rpc.InvokeWithCancellationAsync<ExecuteResourceCommandResponse>(
            "ExecuteResourceCommandAsync",
            [request],
            cancellationToken).ConfigureAwait(false);

        _logger?.LogDebug("Command '{CommandName}' on resource '{ResourceName}' completed with success={Success}", commandName, resourceName, response.Success);

        return response;
    }

    /// <inheritdoc />
    public async Task<WaitForResourceResponse> WaitForResourceAsync(
        string resourceName,
        string status,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsV2)
        {
            return new WaitForResourceResponse
            {
                Success = false,
                ErrorMessage = "Wait command is not supported by the AppHost version. Update the AppHost to use this command."
            };
        }

        var rpc = EnsureConnected();

        _logger?.LogDebug("Waiting for resource '{ResourceName}' to reach status '{Status}' with timeout {Timeout}s", resourceName, status, timeoutSeconds);

        var request = new WaitForResourceRequest
        {
            ResourceName = resourceName,
            Status = status,
            TimeoutSeconds = timeoutSeconds
        };

        var response = await rpc.InvokeWithCancellationAsync<WaitForResourceResponse>(
            "WaitForResourceAsync",
            [request],
            cancellationToken).ConfigureAwait(false);

        _logger?.LogDebug("Wait for resource '{ResourceName}' completed: success={Success}, state={State}", resourceName, response.Success, response.State);

        return response;
    }

    #endregion

    /// <summary>
    /// Disposes the auxiliary backchannel connection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rpc?.Dispose();
        _rpc = null;
    }
}
