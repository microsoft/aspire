// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Hex1b;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// ASP.NET Core middleware that proxies WebSocket connections from the browser
/// to a per-replica HMP v1 producer (the AppHost-owned terminal host) and bridges
/// raw VT byte streams in both directions. The browser identifies the target
/// replica with <c>?resource=&lt;name&gt;&amp;replica=&lt;index&gt;</c>; the
/// actual UDS path is resolved server-side by
/// <see cref="ITerminalConnectionResolver"/> so the dashboard never trusts a
/// browser-supplied filesystem path.
/// </summary>
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

            // Resolve the producer stream entirely server-side. This is the only
            // step that knows the consumer UDS path; nothing about the path leaks
            // out to the browser.
            Stream? producerStream;
            try
            {
                producerStream = await resolver.ConnectAsync(resourceName, replicaIndex, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to resolve terminal connection for {Resource}/{Replica}.", resourceName, replicaIndex);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Terminal is unavailable.").ConfigureAwait(false);
                return;
            }

            if (producerStream is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Terminal is not available for the requested resource and replica.").ConfigureAwait(false);
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var adapter = new Hmp1WorkloadAdapter(producerStream);

            // Cap the upstream handshake so a wedged or mid-recycle terminal
            // host can't tie up the WS, the I/O thread, and an HMP1 connection
            // indefinitely. The dashboard JS has its own reconnect loop and
            // will retry — failing fast here keeps the server healthy under
            // load (e.g. when many tabs reconnect in lockstep after a
            // resource restart).
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await adapter.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning("HMP v1 handshake timed out for {Resource}/{Replica}.", resourceName, replicaIndex);
                await TryCloseAsync(ws, WebSocketCloseStatus.InternalServerError, "Handshake timed out").ConfigureAwait(false);
                producerStream.Dispose();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "HMP v1 handshake failed for {Resource}/{Replica}.", resourceName, replicaIndex);
                await TryCloseAsync(ws, WebSocketCloseStatus.InternalServerError, "Handshake failed").ConfigureAwait(false);
                producerStream.Dispose();
                return;
            }

            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            // Producer → browser: VT bytes flow as Binary frames so xterm.js can
            // hand them straight to terminal.write(). The catch is intentionally
            // broad — a stop/restart of the upstream resource can surface as
            // many different exception types depending on how abruptly the
            // producer-side connection is closed (graceful EOF vs reset vs
            // mid-frame drop). Letting any of those escape this Task.Run would
            // leave an unobserved task exception in the dashboard process; the
            // outer await on outboundPump in the finally block does observe it
            // (so the process won't crash either way), but logging at debug
            // here gives us per-replica diagnostic visibility instead of just
            // "swallowed in finally". OperationCanceledException is the
            // expected shutdown signal and stays silent.
            var outboundPump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        var data = await adapter.ReadOutputAsync(pumpCts.Token).ConfigureAwait(false);
                        if (data.IsEmpty)
                        {
                            // Adapter signals end-of-stream with an empty buffer.
                            break;
                        }

                        if (ws.State != WebSocketState.Open)
                        {
                            break;
                        }

                        await ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, pumpCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Outbound terminal pump ended for {Resource}/{Replica}.", resourceName, replicaIndex);
                }
            }, pumpCts.Token);

            // Browser → producer:
            //   * Binary frames carry raw keystroke bytes (UTF-8 of xterm.js
            //     onData), forwarded as HMP v1 Input frames.
            //   * Text frames carry JSON control messages, currently
            //     {"type":"resize","cols":N,"rows":N}.
            // Splitting keystrokes from controls by frame type avoids the
            // ambiguity of trying to JSON-parse arbitrary user input.
            // Catch is intentionally broad — see outbound pump for the
            // rationale; the outer await observes the exception either way.
            var inboundPump = Task.Run(async () =>
            {
                var pool = ArrayPool<byte>.Shared;
                var buffer = pool.Rent(8192);
                try
                {
                    while (!pumpCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        using var assembly = new ReassembledFrame(pool);
                        ValueWebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(buffer.AsMemory(), pumpCts.Token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }

                            assembly.Append(buffer.AsSpan(0, result.Count));
                        }
                        while (!result.EndOfMessage);

                        var payload = assembly.WrittenMemory;

                        switch (result.MessageType)
                        {
                            case WebSocketMessageType.Binary:
                                if (!payload.IsEmpty)
                                {
                                    await adapter.WriteInputAsync(payload, pumpCts.Token).ConfigureAwait(false);
                                }
                                break;

                            case WebSocketMessageType.Text:
                                await TryHandleControlFrameAsync(payload, adapter, logger, resourceName, replicaIndex, pumpCts.Token).ConfigureAwait(false);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Inbound terminal pump ended for {Resource}/{Replica}.", resourceName, replicaIndex);
                }
                finally
                {
                    pool.Return(buffer);
                }
            }, pumpCts.Token);

            // Wait for either side to drop, then tear down both pumps and the
            // upstream HMP v1 connection.
            try
            {
                await Task.WhenAny(outboundPump, inboundPump).ConfigureAwait(false);
            }
            finally
            {
                pumpCts.Cancel();
                try { await outboundPump.ConfigureAwait(false); } catch { /* swallow */ }
                try { await inboundPump.ConfigureAwait(false); } catch { /* swallow */ }
                producerStream.Dispose();
                await TryCloseAsync(ws, WebSocketCloseStatus.NormalClosure, "Terminal session ended").ConfigureAwait(false);
            }
        }).RequireAuthorization(FrontendAuthorizationDefaults.PolicyName);
    }

    private static async Task TryHandleControlFrameAsync(ReadOnlyMemory<byte> payload,
                                                         Hmp1WorkloadAdapter adapter,
                                                         ILogger logger,
                                                         string resourceName,
                                                         int replicaIndex,
                                                         CancellationToken cancellationToken)
    {
        // Expected control-frame format (UTF-8 text):
        //   { "type": "resize", "cols": <int>, "rows": <int> }
        // Anything else is logged at debug and discarded.
        //
        // We parse synchronously inside this method but await the resize call.
        // The earlier fire-and-forget version (`_ = adapter.ResizeAsync(...).AsTask();`)
        // was a bug: a resize that arrives during a producer recycle (e.g. when
        // the user clicks Stop on the resource and the upstream PTY is being
        // torn down) can throw an exception type that ResizeAsync's internal
        // catch list (IOException, ObjectDisposedException) doesn't cover —
        // notably OperationCanceledException, NullReferenceException from a
        // disposed-mid-write transport, or InvalidOperationException from
        // HMP1Protocol if the stream EOFs between the dimension write and the
        // header write. Those would become unobserved task exceptions in the
        // dashboard process and pollute the AppDomain's UnobservedTaskException
        // event. Awaiting and explicitly catching keeps everything tidy.
        int? cols = null;
        int? rows = null;
        string? type = null;

        try
        {
            var reader = new Utf8JsonReader(payload.Span);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return;
                }

                var propertyName = reader.GetString();
                if (!reader.Read())
                {
                    return;
                }

                switch (propertyName)
                {
                    case "type":
                        type = reader.GetString();
                        break;
                    case "cols":
                        cols = reader.GetInt32();
                        break;
                    case "rows":
                        rows = reader.GetInt32();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Discarding malformed terminal control frame for {Resource}/{Replica}: {Payload}",
                resourceName, replicaIndex, Encoding.UTF8.GetString(payload.Span));
            return;
        }

        if (type != "resize" || cols is not { } c || rows is not { } r || c <= 0 || r <= 0)
        {
            return;
        }

        try
        {
            await adapter.ResizeAsync(c, r, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The upstream HMP1 transport can be in any state when a resize
            // arrives — including mid-recycle on the producer side after a
            // resource Stop. Swallow at debug; the next reconnect will resend
            // dimensions on its own (the dashboard JS calls sendResize() in
            // ws.onopen).
            logger.LogDebug(ex, "Resize for {Resource}/{Replica} ({Cols}x{Rows}) failed.",
                resourceName, replicaIndex, c, r);
        }
    }

    private static async Task TryCloseAsync(WebSocket ws, WebSocketCloseStatus status, string description)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(status, description, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Best effort.
            }
        }
    }

    private sealed class ReassembledFrame : IDisposable
    {
        private readonly ArrayPool<byte> _pool;
        private byte[] _buffer;
        private int _length;

        public ReassembledFrame(ArrayPool<byte> pool)
        {
            _pool = pool;
            _buffer = _pool.Rent(8192);
            _length = 0;
        }

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_length + data.Length > _buffer.Length)
            {
                var bigger = _pool.Rent(Math.Max(_buffer.Length * 2, _length + data.Length));
                Buffer.BlockCopy(_buffer, 0, bigger, 0, _length);
                _pool.Return(_buffer);
                _buffer = bigger;
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                _pool.Return(_buffer);
                _buffer = null!;
            }
        }
    }
}

