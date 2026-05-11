// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using Aspire.Dashboard.Configuration;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// ASP.NET Core middleware that bridges a single browser WebSocket to the
/// upstream <c>Aspire.TerminalHost</c> consumer UDS for the requested
/// resource and replica. The browser speaks HMP v1 directly via its
/// JavaScript HMP1 client (<c>/js/hmp1-client.js</c>); this handler is a
/// dumb byte pump that shuttles raw HMP1 frames in both directions.
/// </summary>
/// <remarks>
/// <para>From the upstream's perspective the browser tab is just another
/// HMP v1 peer in its multi-head roster, so take-control / role-change /
/// state-replay all work end-to-end without any per-connection emulator
/// state in the dashboard process.</para>
/// <para>The browser identifies the target replica via
/// <c>?resource=&lt;name&gt;&amp;replica=&lt;index&gt;</c>; the actual UDS
/// path is resolved server-side by <see cref="ITerminalConnectionResolver"/>
/// so the dashboard never trusts a browser-supplied filesystem path.</para>
/// <para>
/// <b>Why a custom proxy and not Hex1b's <c>Hmp1PresentationAdapter</c>?</b>
/// <c>Hmp1PresentationAdapter</c> is the <i>server</i> side of HMP1: it lives
/// in the process that owns the underlying terminal (Aspire.TerminalHost) and
/// multicasts a single Hex1b terminal to many HMP1 peers. The dashboard never
/// owns a terminal — it sits between two HMP1 endpoints (the browser and the
/// remote terminal host) and relays frames at the byte level. Likewise
/// <c>WebSocketPresentationAdapter</c> is for in-process Hex1b apps that
/// render <i>themselves</i> to a browser via WebSocket; it is not a
/// WebSocket↔stream bridge. Until Hex1b ships a generic HMP1 WebSocket
/// proxy primitive there is no built-in adapter that fits the dashboard's
/// role, so this thin pump is the minimum viable implementation.
/// </para>
/// </remarks>
internal static class TerminalWebSocketProxy
{
    /// <summary>
    /// Maps the terminal WebSocket endpoint at <c>/api/terminal</c>. The handler
    /// requires the same browser authentication as the rest of the Blazor UI.
    /// </summary>
    public static void MapTerminalWebSocket(this WebApplication app)
    {
        app.Map("/api/terminal", async (HttpContext context,
                                       ITerminalConnectionResolver resolver,
                                       ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Aspire.Dashboard.Terminal.TerminalWebSocketProxy");

            // Per-connection correlation id. Lets us tie pump-end logs and
            // any escape-to-Kestrel logs back to a specific browser tab even
            // when many terminals are open. Cheap (16 bytes) and isolates a
            // particular replica's failure from neighbours under load.
            var connectionId = Guid.NewGuid().ToString("n").Substring(0, 8);

            try
            {
                await HandleAsync(context, resolver, logger, connectionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Belt-and-braces: if any exception escapes the inner handler
                // (e.g. from a code path our nested catches missed), log it
                // here at error. Without this the exception would reach
                // Kestrel which can take the entire dashboard down depending
                // on the request state. Phase 9f hardening for the regression
                // where Stop kills the dashboard.
                logger.LogError(ex, "Terminal WebSocket handler {ConnectionId} crashed.", connectionId);

                // Best-effort response if we haven't started writing one yet.
                if (!context.Response.HasStarted)
                {
                    try
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    }
                    catch
                    {
                        // Response could be partially flushed by Kestrel; nothing more to do.
                    }
                }
            }
        }).RequireAuthorization(FrontendAuthorizationDefaults.PolicyName);
    }

    private static async Task HandleAsync(HttpContext context,
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

        var resourceName = context.Request.Query["resource"].ToString();
        var replicaText = context.Request.Query["replica"].ToString();

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing 'resource' query parameter.").ConfigureAwait(false);
            return;
        }

        // Default to replica 0 when omitted (single-replica resources).
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

        // Resolve the upstream stream entirely server-side. This is the only
        // step that knows the consumer UDS path; nothing about the path leaks
        // out to the browser. We resolve eagerly (before accepting the WS)
        // so we can return a proper 404/503 if the resource isn't ready.
        Stream? upstream;
        try
        {
            upstream = await resolver.ConnectAsync(resourceName, replicaIndex, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

        WebSocket ws;
        try
        {
            ws = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to accept terminal WebSocket for {Resource}/{Replica}.", resourceName, replicaIndex);
            try { upstream.Dispose(); } catch { /* swallow */ }
            return;
        }

        // Log at Information so the in/out trace is visible in the
        // default AppHost log without enabling debug logging. Critical
        // forensics when the dashboard process dies on Stop — without
        // this we can't even see whether the connection got established.
        logger.LogInformation("Terminal WS opened for {Resource}/{Replica} ({ConnectionId}).",
            resourceName, replicaIndex, connectionId);

        try
        {
            await BridgeAsync(ws, upstream, logger, connectionId, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            try { upstream.Dispose(); } catch { /* swallow */ }
            logger.LogInformation("Terminal WS closed for {Resource}/{Replica} ({ConnectionId}).",
                resourceName, replicaIndex, connectionId);
        }

        // Best-effort graceful close. Honour CT.None for the close handshake
        // so a server-shutdown request abort doesn't skip the courtesy close.
        if (ws.State == WebSocketState.Open)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                                    "terminal closed",
                                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// Two-task duplex pump: WS→upstream and upstream→WS. Either side
    /// closing/erroring cancels the other. The first task completing is
    /// the trigger; both tasks are awaited (with their own per-task try/
    /// catch) so no exception escapes the bridge.
    /// </summary>
    private static async Task BridgeAsync(WebSocket ws,
                                          Stream upstream,
                                          ILogger logger,
                                          string connectionId,
                                          CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;

        // Browser → upstream. WS frames carry HMP1 payloads from the JS
        // client (Input, Resize, RequestPrimary, ClientHello). Forward
        // verbatim; upstream's Hex1b server speaks HMP1.
        var inbound = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var msg = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (msg.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (msg.Count > 0)
                    {
                        await upstream.WriteAsync(buffer.AsMemory(0, msg.Count), token).ConfigureAwait(false);
                        await upstream.FlushAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                // Catch-all (broadened in Phase 9f). HMP1-protocol exceptions
                // and abrupt-kill races can surface here as IOException,
                // WebSocketException, ObjectDisposedException, or unrelated
                // types depending on the failure mode.
                logger.LogDebug(ex, "Terminal WS inbound pump ended for {ConnectionId} ({ExceptionType}).",
                    connectionId, ex.GetType().FullName);
            }
        }, token);

        // Upstream → browser. Raw HMP1 frames from the terminal host's
        // Hmp1PresentationAdapter; forward as binary WS frames. The JS
        // HMP1 client reassembles them across WS message boundaries.
        var outbound = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = await upstream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // Upstream EOF — terminal host process died or the
                        // replica recycled. Tear the WS down so the JS
                        // reconnect loop kicks in.
                        return;
                    }

                    await ws.SendAsync(new ArraySegment<byte>(buffer, 0, read),
                                       WebSocketMessageType.Binary,
                                       endOfMessage: true,
                                       token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Terminal WS outbound pump ended for {ConnectionId} ({ExceptionType}).",
                    connectionId, ex.GetType().FullName);
            }
        }, token);

        // Whoever finishes first triggers teardown of the other; both are
        // then awaited so we don't leave background tasks running after
        // the request scope ends.
        await Task.WhenAny(inbound, outbound).ConfigureAwait(false);
        try { await linkedCts.CancelAsync().ConfigureAwait(false); } catch { /* swallow */ }
        try { await Task.WhenAll(inbound, outbound).ConfigureAwait(false); } catch { /* swallow */ }
    }
}
