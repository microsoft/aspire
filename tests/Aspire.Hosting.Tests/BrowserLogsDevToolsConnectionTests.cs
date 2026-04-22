// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserLogsDevToolsConnectionTests
{
    [Fact]
    public async Task ConnectAsync_DisposesConnectorWhenConnectFails()
    {
        var connectException = new WebSocketException("Connection refused");
        var connector = new ThrowingClientWebSocketConnector(connectException);

        var exception = await Assert.ThrowsAsync<WebSocketException>(() => ChromeDevToolsConnection.ConnectAsync(
            new Uri("ws://127.0.0.1:12345/devtools/browser/test"),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            CancellationToken.None,
            () => connector));

        Assert.Same(connectException, exception);
        Assert.True(connector.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(15), connector.KeepAliveInterval);
    }

    private sealed class ThrowingClientWebSocketConnector(Exception connectException) : IClientWebSocketConnector
    {
        public bool Disposed { get; private set; }

        public TimeSpan? KeepAliveInterval { get; private set; }

        public void SetKeepAliveInterval(TimeSpan interval)
        {
            KeepAliveInterval = interval;
        }

        public Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken)
        {
            return Task.FromException(connectException);
        }

        public ClientWebSocket DetachConnectedWebSocket()
        {
            throw new InvalidOperationException("A failed connect should not detach a websocket.");
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
