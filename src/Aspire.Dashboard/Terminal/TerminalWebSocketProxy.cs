// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
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
                // Kestrel, which can take the entire dashboard down depending
                // on the request state — observed when an AppHost Stop killed
                // the dashboard via an unhandled terminal-handler exception.
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

        // Cross-Site WebSocket Hijacking defense. Browsers do NOT apply the same-origin
        // policy to WebSockets and ASP.NET Core's antiforgery middleware does not gate
        // WS upgrades (they're GET ... Connection: Upgrade). Without an explicit Origin
        // check, any page loaded in a logged-in developer's browser could
        // `new WebSocket("wss://localhost:<port>/api/terminal?resource=...")`, ride the
        // dashboard's auth cookie, and gain read+write of any WithTerminal() shell.
        //
        // The only legitimate caller of this endpoint is the dashboard's own
        // TerminalView razor component, which is always served from the dashboard's
        // own scheme+host (Request.Scheme + Request.Host). Behind a reverse proxy
        // with UseForwardedHeaders, those reflect the public host so PublicUrl is
        // handled automatically. Browsers always send Origin on WebSocket upgrades,
        // so a missing Origin on a WS request is itself suspicious — reject.
        //
        // See: https://datatracker.ietf.org/doc/html/rfc6455#section-10.2
        if (!IsAllowedOrigin(context, out var originLogValue))
        {
            logger.LogWarning(
                "Rejecting terminal WebSocket upgrade {ConnectionId} with disallowed Origin '{Origin}'.",
                connectionId,
                originLogValue);
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

        // Hex1b's Hmp1PresentationAdapter holds a 1000-frame BoundedChannel
        // per peer and uses TryWrite — when the channel fills it disconnects
        // the peer outright (no backpressure, no drop-oldest). On high-
        // resolution terminals every Output frame is many KB, so a slow
        // browser drain easily fills 1000 frames in a couple of seconds.
        // Bumping the OS receive buffer on the consumer UDS gives Hex1b's
        // write pump much more headroom to drain into the kernel before
        // its channel fills, which directly buys time for the WS proxy to
        // catch up under stress. 1 MB is well above the default (208 KB on
        // Linux, 8 KB on macOS) but still trivial per connection. Best-
        // effort: any failure is non-fatal (the connection still works,
        // just at the default buffer size).
        if (upstream is System.Net.Sockets.NetworkStream ns)
        {
            try
            {
                ns.Socket.ReceiveBufferSize = 1 * 1024 * 1024;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to bump UDS receive buffer for {ConnectionId}.", connectionId);
            }
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

        // Outbound (upstream → browser) is the heavy direction for terminal
        // workloads (fullscreen TUIs emit kilobytes per frame). Use a larger
        // pooled buffer and coalesce consecutive available reads into one
        // WebSocket send. Fewer, larger WS messages = fewer browser dispatch
        // events per second under stress = faster drain = less risk of
        // tripping Hex1b's slow-peer eviction. 256 KB is well above any
        // realistic single Output frame at sane terminal sizes, but still
        // a small per-connection cost.
        const int OutboundBufferSize = 256 * 1024;
        const int InboundBufferSize = 16 * 1024;
        var upstreamNs = upstream as System.Net.Sockets.NetworkStream;

        // Browser → upstream. WS frames carry HMP1 payloads from the JS
        // client (Input, Resize, RequestPrimary, ClientHello). Forward
        // verbatim; upstream's Hex1b server speaks HMP1.
        var inbound = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(InboundBufferSize);
            var bytesIn = 0L;
            string endReason = "unknown";
            Exception? endException = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var msg = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (msg.MessageType == WebSocketMessageType.Close)
                    {
                        endReason = "browser-close";
                        return;
                    }

                    if (msg.Count > 0)
                    {
                        bytesIn += msg.Count;
                        await upstream.WriteAsync(buffer.AsMemory(0, msg.Count), token).ConfigureAwait(false);
                        await upstream.FlushAsync(token).ConfigureAwait(false);
                    }
                }
                endReason = "cancelled";
            }
            catch (OperationCanceledException)
            {
                endReason = "cancelled";
            }
            catch (Exception ex)
            {
                // Catch-all on purpose: HMP1-protocol exceptions and
                // abrupt-kill races can surface here as IOException,
                // WebSocketException, ObjectDisposedException, or unrelated
                // types depending on the failure mode. Letting any of them
                // escape the pump propagates to Kestrel and risks dropping
                // the dashboard request pipeline.
                endReason = "exception";
                endException = ex;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                LogPumpEnd(logger, connectionId, "inbound", endReason, endException, bytesIn, sends: 0, slowSends: 0, maxSendMs: 0);
            }
        }, token);

        // Upstream → browser. Raw HMP1 frames from the terminal host's
        // Hmp1PresentationAdapter; forward as binary WS frames. The JS
        // HMP1 client reassembles them across WS message boundaries.
        var outbound = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(OutboundBufferSize);
            var bytesOut = 0L;
            var sends = 0L;
            var slowSends = 0L;
            var maxSendMs = 0L;
            string endReason = "unknown";
            Exception? endException = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = await upstream.ReadAsync(buffer.AsMemory(0, OutboundBufferSize), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // Upstream EOF — terminal host process died, the
                        // replica recycled, or the host evicted this peer
                        // (e.g. slow-consumer policy). Tear the WS down so
                        // the JS reconnect loop kicks in. The
                        // distinguishing signal (vs. ReadAsync throwing) is
                        // logged via endReason below.
                        endReason = "upstream-eof";
                        return;
                    }

                    // Coalesce: while more data is already available on
                    // the socket without blocking, keep filling the buffer
                    // up to OutboundBufferSize. NetworkStream.DataAvailable
                    // is a synchronous SO_NREAD probe — we use synchronous
                    // Read for the follow-on chunks so we don't pay the
                    // async state-machine cost for what's effectively a
                    // memcpy from the kernel buffer. This collapses bursts
                    // of small upstream writes into one larger WS message,
                    // which under stress is the difference between the
                    // browser keeping up and tripping Hex1b's per-peer
                    // slow-consumer eviction.
                    if (upstreamNs is not null)
                    {
                        while (read < OutboundBufferSize && upstreamNs.DataAvailable)
                        {
                            int more;
                            try
                            {
                                more = upstreamNs.Read(buffer, read, OutboundBufferSize - read);
                            }
                            catch
                            {
                                // Defer to the next outer ReadAsync loop iteration
                                // to surface the failure with proper exception type.
                                break;
                            }
                            if (more <= 0)
                            {
                                break;
                            }
                            read += more;
                        }
                    }

                    bytesOut += read;
                    sends++;

                    var sw = ValueStopwatch.StartNew();
                    await ws.SendAsync(new ArraySegment<byte>(buffer, 0, read),
                                       WebSocketMessageType.Binary,
                                       endOfMessage: true,
                                       token).ConfigureAwait(false);
                    var sendMs = sw.ElapsedMilliseconds;
                    if (sendMs > maxSendMs)
                    {
                        maxSendMs = sendMs;
                    }
                    // 100ms is well above the inter-frame budget for a
                    // 60fps TUI (≈16ms). Sustained slow sends here are
                    // the primary smoking gun for browser-side
                    // backpressure that ultimately triggers an upstream
                    // slow-peer eviction.
                    if (sendMs >= 100)
                    {
                        slowSends++;
                    }
                }
                endReason = "cancelled";
            }
            catch (OperationCanceledException)
            {
                endReason = "cancelled";
            }
            catch (Exception ex)
            {
                endReason = "exception";
                endException = ex;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                LogPumpEnd(logger, connectionId, "outbound", endReason, endException, bytesOut, sends, slowSends, maxSendMs);
            }
        }, token);

        // Whoever finishes first triggers teardown of the other; both are
        // then awaited so we don't leave background tasks running after
        // the request scope ends.
        var firstCompleted = await Task.WhenAny(inbound, outbound).ConfigureAwait(false);
        logger.LogInformation("Terminal bridge first pump ended for {ConnectionId}: {Pump}.",
            connectionId, firstCompleted == inbound ? "inbound" : "outbound");
        try { await linkedCts.CancelAsync().ConfigureAwait(false); } catch { /* swallow */ }
        try { await Task.WhenAll(inbound, outbound).ConfigureAwait(false); } catch { /* swallow */ }
    }

    private static void LogPumpEnd(ILogger logger, string connectionId, string direction, string reason,
                                   Exception? exception, long bytes, long sends, long slowSends, long maxSendMs)
    {
        // Log abnormal terminations at Warning so they show up in default
        // AppHost output, normal terminations at Information. The reason
        // string is the single most useful signal for diagnosing periodic
        // reconnects: "upstream-eof" points at the terminal host /
        // slow-peer policy; "exception" + the type points at a transport
        // failure; "browser-close" is a clean browser-initiated close.
        var slow = direction == "outbound" ? $" sends={sends} slowSends={slowSends} maxSendMs={maxSendMs}" : "";
        var exType = exception?.GetType().FullName ?? "(none)";
        var level = (reason is "exception" or "upstream-eof") ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, exception,
            "Terminal WS {Direction} pump ended for {ConnectionId}: reason={Reason} bytes={Bytes}{SlowInfo} exceptionType={ExceptionType}.",
            direction, connectionId, reason, bytes, slow, exType);
    }

    /// <summary>
    /// Returns true if the request's <c>Origin</c> header is present and matches the
    /// dashboard's own scheme+host (the page that legitimately opens this WebSocket).
    /// Internal so tests can exercise it without spinning up a full WS server.
    /// </summary>
    /// <remarks>
    /// Defense against Cross-Site WebSocket Hijacking. We deliberately do not consult
    /// a configurable allow-list: the only legitimate caller is the dashboard's own
    /// TerminalView component, so the request's scheme+host (which honors forwarded
    /// headers when behind a reverse proxy) is always the correct match. A missing
    /// Origin on a WS upgrade is treated as disallowed because conforming browsers
    /// always include it.
    /// </remarks>
    internal static bool IsAllowedOrigin(HttpContext context, out string originLogValue)
    {
        var origin = context.Request.Headers.Origin.ToString();
        originLogValue = string.IsNullOrEmpty(origin) ? "(none)" : origin;

        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        // Compare against the request's own scheme + host. When behind a reverse proxy
        // with UseForwardedHeaders, these reflect the public-facing values, so the
        // browser's Origin (= the page's public URL) lines up automatically.
        if (!string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedHost = context.Request.Host;
        if (!expectedHost.HasValue)
        {
            return false;
        }

        // Uri.Authority drops a default port (e.g. :443 for https, :80 for http); use
        // Host + ":" + Port directly and let HostString.ToString() apply the same rule.
        // Compare both with and without explicit port to handle the default-port case.
        var originAuthority = originUri.IsDefaultPort
            ? originUri.Host
            : originUri.Host + ":" + originUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var expectedAuthority = expectedHost.ToString();

        return string.Equals(originAuthority, expectedAuthority, StringComparison.OrdinalIgnoreCase);
    }

    private readonly struct ValueStopwatch
    {
        private static readonly double s_timestampToMs = 1000.0 / Stopwatch.Frequency;
        private readonly long _start;
        private ValueStopwatch(long start) => _start = start;
        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());
        public long ElapsedMilliseconds => (long)((Stopwatch.GetTimestamp() - _start) * s_timestampToMs);
    }
}
