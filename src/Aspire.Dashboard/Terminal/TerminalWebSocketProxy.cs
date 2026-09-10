// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Grpc.Core;
using Hex1b;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Presents a remote HMP1 terminal to a browser using Hex1b's HWT1 adapter.
/// </summary>
internal static class TerminalWebSocketProxy
{
    private static readonly TimeSpan s_handshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_sendTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(2);

    public static void MapTerminalWebSocket(this WebApplication app)
    {
        app.Map("/api/terminal", async (HttpContext context,
                                       ITerminalConnectionResolver resolver,
                                       TerminalViewSessionRegistry sessions,
                                       ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Aspire.Dashboard.Terminal.TerminalWebSocketProxy");
            await HandleAsync(context, resolver, sessions, logger, context.TraceIdentifier).ConfigureAwait(false);
        }).RequireAuthorization(FrontendAuthorizationDefaults.PolicyName);

        // AppHost-owned terminals use the existing gRPC connection instead of a
        // consumer UDS. Both transports carry HMP1 into the same per-browser mirror.
        app.Map("/api/apphost-terminal", async (HttpContext context,
                                               IDashboardClient dashboardClient,
                                               TerminalViewSessionRegistry sessions,
                                               ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Aspire.Dashboard.Terminal.TerminalWebSocketProxy");
            await HandleAppHostTerminalAsync(context, dashboardClient, sessions, logger, context.TraceIdentifier).ConfigureAwait(false);
        }).RequireAuthorization(FrontendAuthorizationDefaults.PolicyName);
    }

    internal static async Task HandleAppHostTerminalAsync(HttpContext context,
                                                          IDashboardClient dashboardClient,
                                                          TerminalViewSessionRegistry sessions,
                                                          ILogger logger,
                                                          string connectionId)
    {
        if (!await ValidateUpgradeAsync(context, logger, connectionId).ConfigureAwait(false))
        {
            return;
        }

        var terminalId = context.Request.Query["terminalId"].ToString();
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing 'terminalId' query parameter.").ConfigureAwait(false);
            return;
        }

        if (!TryGetViewSession(context, sessions, out var session))
        {
            return;
        }

        // Keep the call's cancellation token alive for the entire view. Disposing a
        // handshake-only linked token here would detach it from RequestAborted.
        using var attachment = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        attachment.CancelAfter(s_handshakeTimeout);
        Stream upstream;
        try
        {
            upstream = await dashboardClient.AttachTerminalAsync(terminalId, attachment.Token).ConfigureAwait(false);
            attachment.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            await WriteUnavailableAsync(context, logger, connectionId, ex).ConfigureAwait(false);
            return;
        }

        await HandleConnectionAsync(context, upstream, session, logger, connectionId).ConfigureAwait(false);
    }

    internal static async Task HandleAsync(HttpContext context,
                                          ITerminalConnectionResolver resolver,
                                          TerminalViewSessionRegistry sessions,
                                          ILogger logger,
                                          string connectionId)
    {
        if (!await ValidateUpgradeAsync(context, logger, connectionId).ConfigureAwait(false))
        {
            return;
        }

        var resourceName = context.Request.Query["resource"].ToString();
        var replicaText = context.Request.Query["replica"].ToString();
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing 'resource' query parameter.").ConfigureAwait(false);
            return;
        }

        var replicaIndex = 0;
        if (!string.IsNullOrWhiteSpace(replicaText) &&
            !int.TryParse(replicaText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out replicaIndex))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid 'replica' query parameter.").ConfigureAwait(false);
            return;
        }

        if (replicaIndex < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("'replica' must be non-negative.").ConfigureAwait(false);
            return;
        }

        if (!TryGetViewSession(context, sessions, out var session))
        {
            return;
        }

        // The browser supplies resource identity, never a filesystem path. Resolve
        // before accepting the upgrade so unavailable resources retain HTTP errors.
        Stream? upstream;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(s_handshakeTimeout);
            upstream = await resolver.ConnectAsync(resourceName, replicaIndex, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            await WriteUnavailableAsync(context, logger, connectionId, ex).ConfigureAwait(false);
            return;
        }

        if (upstream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Terminal is not available for the requested resource and replica.").ConfigureAwait(false);
            return;
        }

        await HandleConnectionAsync(context, upstream, session, logger, connectionId).ConfigureAwait(false);
    }

    private static bool TryGetViewSession(HttpContext context, TerminalViewSessionRegistry sessions, out TerminalViewSession? session)
    {
        session = null;
        if (!context.Request.Query.TryGetValue("viewId", out var viewId))
        {
            // Direct consumers are interactive; this flag is per-view UI policy,
            // not an authorization boundary for the authenticated terminal owner.
            return true;
        }

        var endpoint = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        if (sessions.TryGet(viewId.ToString(), endpoint, out session))
        {
            return true;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return false;
    }

    private static async Task<bool> ValidateUpgradeAsync(HttpContext context, ILogger logger, string connectionId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket upgrade request.").ConfigureAwait(false);
            return false;
        }

        // Browsers send cookies on cross-origin WebSocket upgrades, and antiforgery
        // middleware does not protect these GET requests. Validate Origin before
        // opening either transport, including when frontend auth is disabled.
        // See https://datatracker.ietf.org/doc/html/rfc6455#section-10.2.
        if (!IsAllowedOrigin(context, out var originLogValue))
        {
            logger.LogWarning(
                "Rejecting terminal WebSocket upgrade {ConnectionId} with disallowed Origin '{Origin}'.",
                connectionId, originLogValue);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin not allowed.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static async Task WriteUnavailableAsync(HttpContext context, ILogger logger, string connectionId, Exception exception)
    {
        logger.LogWarning(exception, "Failed to attach terminal ({ConnectionId}).", connectionId);
        var rpc = exception as RpcException ?? exception.InnerException as RpcException;
        context.Response.StatusCode = rpc?.StatusCode == StatusCode.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("Terminal is unavailable.").ConfigureAwait(false);
    }

    private static async Task HandleConnectionAsync(HttpContext context, Stream upstream, TerminalViewSession? session, ILogger logger, string connectionId)
    {
        await using var upstreamLifetime = upstream.ConfigureAwait(false);
        var workload = new Hmp1WorkloadAdapter(new Hmp1ClientOptions
        {
            StreamFactory = _ => Task.FromResult(upstream),
            DisplayName = "Aspire dashboard"
        });
        await using var workloadLifetime = workload.ConfigureAwait(false);

        // AttachTerminalAsync sends the selector but does not read the response.
        // Complete the HMP handshake before upgrading so gRPC NotFound/Unavailable
        // statuses can still be returned as HTTP errors.
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            handshake.CancelAfter(s_handshakeTimeout);
            await workload.ConnectAsync(handshake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception) when (upstream is GrpcTerminalClientStream { TerminalEnded: true })
        {
            session?.MarkEnded();
            // A completed AppHost terminal cannot perform HMP's initial replay.
            // Keep an empty viewer open until the user closes it, without creating
            // a fake workload or adding Aspire messages to the HWT protocol.
            using var endedSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await PumpViewAsync(endedSocket, presentation: null, workload: null, upstream, session, logger, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or RpcException or InvalidOperationException or
            InvalidDataException or JsonException or OperationCanceledException)
        {
            await WriteUnavailableAsync(context, logger, connectionId, ex).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        logger.LogDebug("Terminal view opened ({ConnectionId}).", connectionId);
        try
        {
            await BridgeAsync(socket, workload, upstream, session, logger, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Also cover mirror construction and disposal: these run outside the
            // pump lifetime, but must not escape an already-upgraded request.
            logger.LogError(ex, "Terminal view failed ({ConnectionId}).", connectionId);
            await CloseOutputAsync(socket, WebSocketCloseStatus.InternalServerError, "Terminal view failed", logger).ConfigureAwait(false);
        }
        finally
        {
            logger.LogDebug("Terminal view closed ({ConnectionId}).", connectionId);
        }
    }

    private static async Task BridgeAsync(WebSocket socket, Hmp1WorkloadAdapter workload, Stream upstream,
        TerminalViewSession? session, ILogger logger, CancellationToken cancellationToken)
    {
        // The remote producer owns geometry, primary role and graphics checkpoints.
        // This mirror belongs only to this browser; disposing it releases the HMP
        // peer and its transport, never the producer or the creator's terminal.
        // https://github.com/mitchdenny/hex1b/blob/798b26c/docs/web-terminal.md
        var presentation = new Hwt1PresentationAdapter();
        await using var presentationLifetime = presentation.ConfigureAwait(false);
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithScrollback(10000)
            .Build();
        await using var terminalLifetime = terminal.ConfigureAwait(false);
        await PumpViewAsync(socket, presentation, workload, upstream, session, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpViewAsync(WebSocket socket, Hwt1PresentationAdapter? presentation, Hmp1WorkloadAdapter? workload,
        Stream upstream, TerminalViewSession? session, ILogger logger, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var send = presentation is null
            ? Task.Delay(Timeout.InfiniteTimeSpan, sending.Token)
            : SendFramesAsync(socket, presentation, sending.Token);
        var receive = ReceiveMessagesAsync(socket, presentation, workload, session, upstream as GrpcTerminalClientStream, stopping.Token);
        var disconnected = workload is null
            ? Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token)
            : WaitForTransportDisconnectAsync(workload, upstream, session, logger, stopping.Token);
        var tasks = new[] { send, receive, disconnected };
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "Terminal closed";
        try
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (completed == disconnected)
            {
                closeStatus = WebSocketCloseStatus.EndpointUnavailable;
                closeReason = "Terminal transport disconnected";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser or the dashboard ended this request.
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or RpcException)
        {
            logger.LogDebug(ex, "Terminal transport disconnected.");
            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
            closeReason = "Terminal transport disconnected";
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Terminal view timed out.");
            closeStatus = WebSocketCloseStatus.PolicyViolation;
            closeReason = "Terminal connection timed out";
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or ArgumentException)
        {
            logger.LogWarning(ex, "Invalid terminal input or state.");
            closeStatus = WebSocketCloseStatus.PolicyViolation;
            closeReason = "Invalid terminal input or state";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Terminal view failed.");
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeReason = "Terminal view failed";
        }
        finally
        {
            // Send the close frame before cancelling ReceiveAsync, which can abort
            // the socket. Stop and join the binary sender first because WebSocket
            // permits only one pending send operation, including CloseOutputAsync.
            try
            {
                await sending.CancelAsync().ConfigureAwait(false);
                await ObserveTeardownAsync(send, logger).ConfigureAwait(false);
                await CloseOutputAsync(socket, closeStatus, closeReason, logger).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await stopping.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    await ObserveTeardownAsync(Task.WhenAll(tasks), logger).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task WaitForTransportDisconnectAsync(Hmp1WorkloadAdapter workload, Stream upstream,
        TerminalViewSession? session, ILogger logger, CancellationToken cancellationToken)
    {
        await workload.DisconnectedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (upstream is not GrpcTerminalClientStream grpc)
        {
            return;
        }

        // HMP Exit may precede the AppHost's terminal-level Ended notification.
        // The HMP reader has stopped by DisconnectedTask, so it is safe to drain
        // remaining opaque bytes here without concurrent reads or HMP parsing.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_handshakeTimeout);
        try
        {
            var buffer = new byte[4096];
            while (await grpc.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) != 0)
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Timed out waiting for the AppHost terminal's final transport status.");
            return;
        }

        if (grpc.TerminalEnded)
        {
            session?.MarkEnded();
            // HWT currently has no authoritative ended state. Retain this viewer
            // and its last available projection until the user dismisses it. The
            // receive loop rejects mutations after this flag is set, but still
            // serves local selection/copy/history operations on the mirror.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ObserveTeardownAsync(Task task, ILogger logger)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Observe all tasks even if a second failure occurs while unwinding
            // the first. Neither pump is allowed to outlive this viewer.
            logger.LogDebug(ex, "Terminal pump ended during view teardown.");
        }
    }

    private static async Task CloseOutputAsync(WebSocket socket, WebSocketCloseStatus status, string reason, ILogger logger)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(s_closeTimeout);
        try
        {
            await socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Terminal close handshake failed.");
            socket.Abort();
        }
    }

    private static async Task SendFramesAsync(WebSocket socket, Hwt1PresentationAdapter presentation, CancellationToken cancellationToken)
    {
        while (true)
        {
            // HWT1 frames are ordered complete binary messages. The adapter handles
            // acknowledgements and coalesces state while blocked; never drop frames.
            var frame = await presentation.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_sendTimeout);
            try
            {
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The browser did not receive a terminal frame.");
            }
        }
    }

    private static async Task ReceiveMessagesAsync(WebSocket socket, Hwt1PresentationAdapter? presentation,
        Hmp1WorkloadAdapter? workload, TerminalViewSession? session, GrpcTerminalClientStream? grpc, CancellationToken cancellationToken)
    {
        // HWT1 commands are UTF-8 JSON, e.g. {"type":"ack","revision":1}. WebSocket
        // fragmentation can split anywhere, including within a UTF-8 code point.
        // Reassemble the whole bounded message before handing it to the public API.
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    throw new InvalidDataException("Terminal input exceeds 64 KiB.");
                }

                result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Expected a terminal JSON command.");
                }
                length += result.Count;
            }
            while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var type = document.RootElement.GetProperty("type").GetString();
            var mutatesWorkload = type switch
            {
                "input" or "paste" or "key" or "mouse" or "resize" or "requestPrimary" => true,
                "ack" or "resync" or "viewport" or "selection" or "copy" => false,
                _ => throw new InvalidDataException("Unknown terminal command.")
            };

            // Read policy after receiving the complete command: a paste or pointer
            // action started before the component became read-only may arrive later.
            // Do not close the view or block ACKs, selection, copying, or output.
            if (presentation is null || mutatesWorkload &&
                (session?.ReadOnly == true || grpc?.TerminalEnded == true || workload?.IsConnected == false))
            {
                continue;
            }

            await presentation.HandleMessageAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsAllowedOrigin(HttpContext context, out string originLogValue)
    {
        return WebSocketOriginValidator.IsSameOrigin(context, out originLogValue);
    }
}
