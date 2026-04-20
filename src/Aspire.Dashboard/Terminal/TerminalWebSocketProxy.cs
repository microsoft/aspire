// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CA2007 // Consider calling ConfigureAwait
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aspire.Terminal;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// ASP.NET Core middleware that proxies WebSocket connections from the browser
/// to a Unix domain socket speaking the Aspire Terminal Protocol.
/// </summary>
internal static class TerminalWebSocketProxy
{
    /// <summary>
    /// Maps the terminal WebSocket endpoint at /api/terminal.
    /// </summary>
    public static void MapTerminalWebSocket(this WebApplication app)
    {
        app.Map("/api/terminal", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connection required.");
                return;
            }

            var socketPath = context.Request.Query["socketPath"].FirstOrDefault();
            if (string.IsNullOrEmpty(socketPath))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("socketPath query parameter is required.");
                return;
            }

            if (!File.Exists(socketPath))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Terminal socket not found.");
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await ProxyTerminalAsync(webSocket, socketPath, context.RequestAborted);
        });
    }

    private static async Task ProxyTerminalAsync(WebSocket webSocket, string socketPath, CancellationToken ct)
    {
        using var udsSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await udsSocket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        }
        catch (SocketException)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Cannot connect to terminal socket", ct);
            return;
        }

        await using var udsStream = new NetworkStream(udsSocket, ownsSocket: false);
        var reader = new TerminalFrameReader(udsStream);
        var writer = new TerminalFrameWriter(udsStream);

        // Read HELLO from UDS
        var helloFrame = await reader.ReadFrameAsync(ct);
        if (helloFrame is null || helloFrame.Value.Type != TerminalProtocol.MessageType.Hello)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, "No HELLO from terminal", ct);
            return;
        }

        // Parse HELLO but don't send it to the browser — xterm.js doesn't need it
        var (_, cols, rows, _) = helloFrame.Value.ParseHello();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // UDS → WebSocket (terminal output to browser)
        var outputTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var frame = await reader.ReadFrameAsync(cts.Token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        break;
                    }

                    switch (frame.Value.Type)
                    {
                        case TerminalProtocol.MessageType.Data:
                            await webSocket.SendAsync(
                                frame.Value.Payload,
                                WebSocketMessageType.Binary,
                                endOfMessage: true,
                                cts.Token).ConfigureAwait(false);
                            break;

                        case TerminalProtocol.MessageType.Exit:
                        case TerminalProtocol.MessageType.Close:
                            await cts.CancelAsync().ConfigureAwait(false);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException) { }
        }, cts.Token);

        // WebSocket → UDS (browser input to terminal)
        var inputTask = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await writer.WriteCloseAsync(cts.Token).ConfigureAwait(false);
                        await cts.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Check for JSON resize message from xterm.js
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (TryParseResize(text, out var newCols, out var newRows))
                        {
                            await writer.WriteResizeAsync((ushort)newCols, (ushort)newRows, cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        // Text input — convert to bytes and send as DATA
                        var inputBytes = Encoding.UTF8.GetBytes(text);
                        await writer.WriteDataAsync(inputBytes, cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        // Binary input
                        await writer.WriteDataAsync(
                            buffer.AsMemory(0, result.Count),
                            cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException) { }
        }, cts.Token);

        await Task.WhenAny(outputTask, inputTask).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        try { await Task.WhenAll(outputTask, inputTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        if (webSocket.State == WebSocketState.Open)
        {
            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Terminal closed", CancellationToken.None);
            }
            catch { /* best effort */ }
        }
    }

    private static bool TryParseResize(string text, out int cols, out int rows)
    {
        cols = 0;
        rows = 0;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == "resize" &&
                doc.RootElement.TryGetProperty("cols", out var colsEl) &&
                doc.RootElement.TryGetProperty("rows", out var rowsEl))
            {
                cols = colsEl.GetInt32();
                rows = rowsEl.GetInt32();
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON, treat as text input
        }

        return false;
    }
}
