// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Hex1b;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Presents an AppHost-owned HMP1 terminal to a browser using Hex1b's HWT1 adapter.
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
                                       ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Aspire.Dashboard.Terminal.TerminalWebSocketProxy");
            await HandleAsync(context, resolver, logger, context.TraceIdentifier).ConfigureAwait(false);
        }).RequireAuthorization(FrontendAuthorizationDefaults.PolicyName);
    }

    internal static async Task HandleAsync(HttpContext context,
                                          ITerminalConnectionResolver resolver,
                                          ILogger logger,
                                          string connectionId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket upgrade request.").ConfigureAwait(false);
            return;
        }

        // Browsers send cookies on cross-origin WebSocket upgrades, and antiforgery
        // middleware does not protect these GET requests. Validate Origin before
        // opening any resource connection, including when frontend auth is disabled.
        // See https://datatracker.ietf.org/doc/html/rfc6455#section-10.2.
        if (!IsAllowedOrigin(context, out var originLogValue))
        {
            logger.LogWarning(
                "Rejecting terminal WebSocket upgrade {ConnectionId} with disallowed Origin '{Origin}'.",
                connectionId, originLogValue);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin not allowed.").ConfigureAwait(false);
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

        // The browser supplies resource identity, never a filesystem path. Resolve
        // before accepting the upgrade so unavailable resources retain HTTP errors.
        Stream? upstream;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(s_handshakeTimeout);
            upstream = await resolver.ConnectAsync(resourceName, replicaIndex, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException ||
            ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to resolve terminal connection for {Resource}/{Replica}.", resourceName, replicaIndex);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Terminal is unavailable.").ConfigureAwait(false);
            return;
        }

        if (upstream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Terminal is not available for the requested resource and replica.").ConfigureAwait(false);
            return;
        }

        await using var upstreamLifetime = upstream.ConfigureAwait(false);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "Terminal closed";
        try
        {
            logger.LogDebug("Terminal view opened for {Resource}/{Replica} ({ConnectionId}).",
                resourceName, replicaIndex, connectionId);
            await BridgeAsync(socket, upstream, logger, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser or the dashboard has ended this request.
        }
        catch (Exception ex) when (ex is IOException or WebSocketException)
        {
            logger.LogDebug(ex, "Terminal transport disconnected ({ConnectionId}).", connectionId);
            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
            closeReason = "Terminal transport disconnected";
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Terminal view timed out ({ConnectionId}).", connectionId);
            closeStatus = WebSocketCloseStatus.PolicyViolation;
            closeReason = "Terminal connection timed out";
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or ArgumentException)
        {
            logger.LogWarning(ex, "Invalid terminal input or state ({ConnectionId}).", connectionId);
            closeStatus = WebSocketCloseStatus.PolicyViolation;
            closeReason = "Invalid terminal input or state";
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(s_closeTimeout);
                try
                {
                    await socket.CloseOutputAsync(closeStatus, closeReason, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    logger.LogDebug(ex, "Terminal close handshake failed ({ConnectionId}).", connectionId);
                    socket.Abort();
                }
            }
        }
    }

    internal static async Task BridgeAsync(WebSocket socket, Stream upstream, ILogger logger, CancellationToken cancellationToken)
    {
        var workload = new Hmp1WorkloadAdapter(new Hmp1ClientOptions
        {
            StreamFactory = _ => Task.FromResult(upstream),
            DisplayName = "Aspire dashboard"
        });
        await using var workloadLifetime = workload.ConfigureAwait(false);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            handshake.CancelAfter(s_handshakeTimeout);
            try
            {
                await workload.ConnectAsync(handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The terminal host did not complete its handshake.");
            }
        }

        // A direct HMP1 workload preserves the producer's confirmed primary role,
        // geometry and graphics checkpoints. The mirror belongs to this browser;
        // disposing it disconnects the peer, not the AppHost-owned terminal.
        // https://github.com/mitchdenny/hex1b/blob/1f47fd9a/docs/web-terminal.md
        var presentation = new Hwt1PresentationAdapter();
        await using var presentationLifetime = presentation.ConfigureAwait(false);
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithScrollback(10000)
            .Build();
        await using var terminalLifetime = terminal.ConfigureAwait(false);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new[]
        {
            SendFramesAsync(socket, presentation, stopping.Token),
            ReceiveMessagesAsync(socket, presentation, stopping.Token),
            workload.DisconnectedTask.WaitAsync(stopping.Token)
        };
        try
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or TimeoutException or
                InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or
                FormatException or ArgumentException)
            {
                // Preserve the first failure while observing errors from both pumps.
                logger.LogDebug(ex, "Terminal pump ended during view teardown.");
            }
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

    private static async Task ReceiveMessagesAsync(WebSocket socket, Hwt1PresentationAdapter presentation, CancellationToken cancellationToken)
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

            await presentation.HandleMessageAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsAllowedOrigin(HttpContext context, out string originLogValue)
    {
        return WebSocketOriginValidator.IsSameOrigin(context, out originLogValue);
    }
}
