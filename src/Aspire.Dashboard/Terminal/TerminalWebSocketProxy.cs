// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// ASP.NET Core middleware that proxies WebSocket connections from the browser
/// to a terminal-host UDS using HMP v1.
///
/// PHASE 7 PENDING: during the WithTerminal end-to-end refactor this proxy is
/// stubbed to return 503. Once the per-replica HMP v1 wire-up lands (Phases 4 + 5),
/// the proxy will look up the consumer UDS path for the requested resource/replica
/// via the AppHost backchannel and bridge the WebSocket to a Hex1b HMP v1 client.
/// </summary>
internal static class TerminalWebSocketProxy
{
    /// <summary>
    /// Maps the terminal WebSocket endpoint at /api/terminal as a 503 stub.
    /// </summary>
    public static void MapTerminalWebSocket(this WebApplication app)
    {
        app.Map("/api/terminal", async (HttpContext context) =>
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync(
                "Terminal endpoint is not yet available in this build. " +
                "Pending Phase 7 of the WithTerminal end-to-end work.").ConfigureAwait(false);
        });
    }
}
