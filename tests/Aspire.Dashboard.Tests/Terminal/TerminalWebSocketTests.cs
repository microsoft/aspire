// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Tests.Shared;
using Hex1b.Input;
using Xunit;

namespace Aspire.Dashboard.Tests.Terminal;

public class TerminalWebSocketTests(ITestOutputHelper output)
{
    [Fact]
    public async Task BrowserView_PreservesRemotePrimaryAndResizeAcrossReconnect()
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var first = await host.ConnectBrowserAsync(timeout.Token);
        var initial = await ReadUntilAsync(first, frame => frame.GetProperty("peer").GetProperty("id").ValueKind == JsonValueKind.String, timeout.Token);
        Assert.Equal(100, initial.GetProperty("columns").GetInt32());
        Assert.Equal(30, initial.GetProperty("rows").GetInt32());

        // A browser resize cannot seize the remote producer's primary role.
        await SendAsync(first, """{"type":"resize","columns":80,"rows":24}""", timeout.Token);
        await SendAsync(first, """{"type":"requestPrimary","columns":80,"rows":24}""", timeout.Token);
        var primary = await ReadUntilAsync(first, frame => frame.GetProperty("peer").GetProperty("isPrimary").GetBoolean(), timeout.Token);
        Assert.Equal(80, primary.GetProperty("columns").GetInt32());
        Assert.Equal(24, primary.GetProperty("rows").GetInt32());
        Assert.Equal(primary.GetProperty("peer").GetProperty("id").GetString(), host.Presentation.PrimaryPeerId);

        using var second = await host.ConnectBrowserAsync(timeout.Token);
        var viewer = await ReadUntilAsync(second, frame => frame.GetProperty("peer").GetProperty("id").ValueKind == JsonValueKind.String, timeout.Token);
        Assert.False(viewer.GetProperty("peer").GetProperty("isPrimary").GetBoolean());
        Assert.Equal(80, viewer.GetProperty("columns").GetInt32());

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnect", timeout.Token);
        await ReadUntilAsync(second, frame => frame.GetProperty("peer").GetProperty("primaryId").ValueKind == JsonValueKind.Null, timeout.Token);
        await SendAsync(second, """{"type":"requestPrimary","columns":132,"rows":30}""", timeout.Token);
        var takeover = await ReadUntilAsync(second, frame => frame.GetProperty("peer").GetProperty("isPrimary").GetBoolean(), timeout.Token);
        Assert.Equal(132, takeover.GetProperty("columns").GetInt32());
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Fact]
    public async Task BrowserView_ReassemblesFragmentedUtf8Input()
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var browser = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(browser, _ => true, timeout.Token);

        var message = Encoding.UTF8.GetBytes("{\"type\":\"input\",\"text\":\"hello \u00e9\"}");
        var split = Array.IndexOf(message, (byte)0xc3) + 1;
        await browser.SendAsync(message.AsMemory(0, split), WebSocketMessageType.Text, false, timeout.Token);
        await browser.SendAsync(message.AsMemory(split), WebSocketMessageType.Text, true, timeout.Token);

        var input = new StringBuilder();
        while (input.Length < "hello \u00e9".Length)
        {
            var inputEvent = await host.Workload.InputEvents.ReadAsync(timeout.Token);
            if (inputEvent is Hex1bKeyEvent key)
            {
                input.Append(key.Text);
            }
        }
        Assert.Equal("hello \u00e9", input.ToString());
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserView_RejectsInvalidMessageTypeOrOversizedInput(bool oversized)
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var browser = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(browser, _ => true, timeout.Token);

        if (oversized)
        {
            await browser.SendAsync(new byte[64 * 1024], WebSocketMessageType.Text, false, timeout.Token);
        }
        else
        {
            await browser.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, timeout.Token);
        }

        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await browser.ReceiveAsync(buffer, timeout.Token);
        }
        while (result.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
    }

    [Theory]
    [InlineData("\u001bP7;1q\"1;1;2;6#1;2;100;0;0#1BB\u001b\\")]
    [InlineData("\u001b_Ga=T,f=32,s=1,v=1,i=7,p=11,C=1,q=2;/wAA/w==\u001b\\")]
    public async Task BrowserView_ProjectsSixelAndKittyGraphics(string sequence)
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var browser = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(browser, _ => true, timeout.Token);

        host.Workload.Write(sequence);
        var graphics = await ReadUntilAsync(browser, frame => frame.GetProperty("placements").GetArrayLength() > 0, timeout.Token);
        Assert.Single(graphics.GetProperty("placements").EnumerateArray());
        Assert.Single(graphics.GetProperty("images").EnumerateArray());
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);

        using var reconnected = await host.ConnectBrowserAsync(timeout.Token);
        var restored = await ReadUntilAsync(reconnected, frame => frame.GetProperty("placements").GetArrayLength() > 0, timeout.Token);
        Assert.Single(restored.GetProperty("placements").EnumerateArray());
        Assert.Single(restored.GetProperty("images").EnumerateArray());
        await reconnected.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BrowserView_PreservesKittyImageForLatePeerAndPlacementUpdates(bool alternateScreen, bool nativeSize)
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var first = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(first, _ => true, timeout.Token);

        if (alternateScreen)
        {
            host.Workload.Write("\u001b[?1049h");
        }

        // Kitty transmits pixels with a=t, then reuses the image id in a=p
        // placement commands that contain no image data. Like KgpCloudDemo,
        // synchronized frames use lowercase d=a to remove placements, not pixels.
        // https://sw.kovidgoyal.net/kitty/graphics-protocol/#displaying-images-on-screen
        var sizing = nativeSize ? "" : ",c=2,r=2";
        host.Workload.Write("\u001b[?2026h\u001b_Ga=t,f=32,t=d,s=1,v=1,i=7300,q=2;/wAA/w==\u001b\\" +
            "\u001b_Ga=d,d=a,q=2\u001b\\" +
            $"\u001b[2;3H\u001b_Ga=p,i=7300{sizing},C=1,q=2\u001b\\\u001b[?2026l");
        var initial = await ReadUntilAsync(first, HasPlacement, timeout.Token);
        AssertImageIncluded(initial);

        using var second = await host.ConnectBrowserAsync(timeout.Token);
        var late = await ReadUntilAsync(second, HasPlacement, timeout.Token);
        AssertImageIncluded(late);
        Assert.Equal(initial.GetProperty("placements")[0].GetProperty("x").GetDouble(),
            late.GetProperty("placements")[0].GetProperty("x").GetDouble());

        host.Workload.Write("\u001b[?2026h\u001b_Ga=d,d=a,q=2\u001b\\" +
            $"\u001b[2;8H\u001b_Ga=p,i=7300{sizing},C=1,q=2\u001b\\\u001b[?2026l");
        var originalX = initial.GetProperty("placements")[0].GetProperty("x").GetDouble();
        var updates = await Task.WhenAll(
            ReadUntilAsync(first, HasMovedPlacement, timeout.Token),
            ReadUntilAsync(second, HasMovedPlacement, timeout.Token));
        Assert.Equal(updates[0].GetProperty("placements")[0].GetProperty("x").GetDouble(),
            updates[1].GetProperty("placements")[0].GetProperty("x").GetDouble());

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnect", timeout.Token);
        using var reconnected = await host.ConnectBrowserAsync(timeout.Token);
        var restored = await ReadUntilAsync(reconnected, HasMovedPlacement, timeout.Token);
        AssertImageIncluded(restored);
        await reconnected.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);

        static bool HasPlacement(JsonElement frame) => frame.GetProperty("placements").GetArrayLength() == 1;

        bool HasMovedPlacement(JsonElement frame) =>
            HasPlacement(frame) && frame.GetProperty("placements")[0].GetProperty("x").GetDouble() > originalX;

        static void AssertImageIncluded(JsonElement frame)
        {
            var image = Assert.Single(frame.GetProperty("images").EnumerateArray());
            Assert.Equal(1, image.GetProperty("width").GetInt32());
            Assert.Equal(1, image.GetProperty("height").GetInt32());
            Assert.Equal(4, image.GetProperty("byteLength").GetInt32());
            Assert.Equal(image.GetProperty("key").GetString(), frame.GetProperty("placements")[0].GetProperty("key").GetString());
        }
    }

    [Fact]
    public async Task BrowserView_AttachingDuringKittyPlacementReplacementRetainsPixels()
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var first = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(first, _ => true, timeout.Token);

        host.Workload.Write("\u001b[?1049h\u001b_Ga=t,f=32,t=d,s=1,v=1,i=7300,q=2;/wAA/w==\u001b\\" +
            "\u001b[2;3H\u001b_Ga=p,i=7300,C=1,q=2\u001b\\");
        var initial = await ReadUntilAsync(first, frame => frame.GetProperty("placements").GetArrayLength() == 1, timeout.Token);
        var originalX = initial.GetProperty("placements")[0].GetProperty("x").GetDouble();

        // An animation can clear placements inside a synchronized-output frame
        // before emitting replacements. Lowercase d=a must leave the uploaded
        // pixels available to viewers that attach during that interval.
        host.Workload.Write("\u001b[?2026h\u001b_Ga=d,d=a,q=2\u001b\\\u001b[Hpalette-cleared");
        await host.WaitForProducerTextAsync("palette-cleared", timeout.Token);
        using var late = await host.ConnectBrowserAsync(timeout.Token);
        await host.WaitForPeerHandshakesAsync(timeout.Token);

        host.Workload.Write("\u001b[2;8H\u001b_Ga=p,i=7300,C=1,q=2\u001b\\\u001b[?2026l");
        var original = await ReadUntilAsync(first, frame => frame.GetProperty("placements").GetArrayLength() == 1 &&
            frame.GetProperty("placements")[0].GetProperty("x").GetDouble() > originalX, timeout.Token);
        var restored = await ReadUntilAsync(late, frame => frame.GetProperty("placements").GetArrayLength() == 1, timeout.Token);
        var image = Assert.Single(restored.GetProperty("images").EnumerateArray());
        Assert.Equal(4, image.GetProperty("byteLength").GetInt32());
        Assert.Equal(original.GetProperty("placements")[0].GetProperty("x").GetDouble(),
            restored.GetProperty("placements")[0].GetProperty("x").GetDouble());
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
        await late.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(27)]
    public async Task BrowserView_AttachingDuringKittyPlacementCommandReplaysCompleteSequence(int splitIndex)
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var first = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(first, _ => true, timeout.Token);

        host.Workload.Write("\u001b[?1049h\u001b_Ga=T,f=32,t=d,s=1,v=1,i=7300,p=11,C=1,q=2;/wAA/w==\u001b\\");
        var initial = await ReadUntilAsync(first, frame => frame.GetProperty("placements").GetArrayLength() == 1, timeout.Token);
        var originalX = initial.GetProperty("placements")[0].GetProperty("x").GetDouble();

        // PTY reads can split ESC_Ga=p,...ESC\ within its introducer, fields or
        // terminator. A new HMP peer needs that incomplete parser prefix as well
        // as the screen checkpoint, or the suffix becomes ordinary screen text.
        const string placement = "\u001b_Ga=p,i=7300,p=11,C=1,q=2\u001b\\";
        host.Workload.Write("\u001b[Hprefix-ready\u001b[2;8H" + placement[..splitIndex]);
        await host.WaitForProducerTextAsync("prefix-ready", timeout.Token);
        using var late = await host.ConnectBrowserAsync(timeout.Token);
        await host.WaitForPeerHandshakesAsync(timeout.Token);

        host.Workload.Write(placement[splitIndex..]);
        var updates = await Task.WhenAll(
            ReadUntilAsync(first, HasMovedPlacement, timeout.Token),
            ReadUntilAsync(late, HasMovedPlacement, timeout.Token));
        Assert.Equal(updates[0].GetProperty("placements")[0].GetProperty("x").GetDouble(),
            updates[1].GetProperty("placements")[0].GetProperty("x").GetDouble());
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
        await late.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);

        bool HasMovedPlacement(JsonElement frame) =>
            frame.GetProperty("placements").GetArrayLength() == 1 &&
            frame.GetProperty("placements")[0].GetProperty("x").GetDouble() > originalX;
    }

    [Fact]
    public async Task BrowserView_PreservesHyperlinkDestinationChangesAcrossReconnect()
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var browser = await host.ConnectBrowserAsync(timeout.Token);
        await ReadUntilAsync(browser, _ => true, timeout.Token);

        // OSC 8: ESC ] 8 ; parameters ; URI ST text ESC ] 8 ; ; ST.
        // Replacing only the destination must update HWT metadata even when
        // the visible cells remain identical.
        host.Workload.Write("\u001b[H\u001b]8;;https://example.com/first\u001b\\link\u001b]8;;\u001b\\");
        var initial = await ReadUntilAsync(browser, frame => frame.GetProperty("hyperlinks").GetArrayLength() > 0, timeout.Token);
        AssertLink(initial, "https://example.com/first");

        host.Workload.Write("\u001b[H\u001b]8;;https://example.com/second\u001b\\link\u001b]8;;\u001b\\");
        var changed = await ReadUntilAsync(browser, frame => frame.GetProperty("hyperlinks").EnumerateArray()
            .Any(link => link.GetProperty("uri").GetString() == "https://example.com/second"), timeout.Token);
        AssertLink(changed, "https://example.com/second");
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnect", timeout.Token);

        using var reconnected = await host.ConnectBrowserAsync(timeout.Token);
        var restored = await ReadUntilAsync(reconnected, frame => frame.GetProperty("hyperlinks").GetArrayLength() > 0, timeout.Token);
        AssertLink(restored, "https://example.com/second");
        await reconnected.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);

        static void AssertLink(JsonElement frame, string uri)
        {
            var link = Assert.Single(frame.GetProperty("hyperlinks").EnumerateArray());
            Assert.Equal(uri, link.GetProperty("uri").GetString());
            Assert.Equal(0, link.GetProperty("row").GetInt32());
            Assert.Equal(0, link.GetProperty("startColumn").GetInt32());
            Assert.Equal(4, link.GetProperty("endColumn").GetInt32());
        }
    }

    [Fact]
    public async Task BrowserView_RequiresAuthenticationBeforeConnectingToProducer()
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);

        await Assert.ThrowsAsync<WebSocketException>(() => host.ConnectBrowserAsync(timeout.Token));

        Assert.Equal(0, host.ConnectionCount);
    }

    [Fact]
    public async Task BrowserView_ProducerDisconnectClosesBrowserWhileWaitingForAcknowledgement()
    {
        await using var host = new TerminalTestHost(output, requireAuthentication: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token);
        using var browser = await host.ConnectBrowserAsync(timeout.Token);
        var buffer = new byte[64 * 1024];
        // A snapshot can exceed one receive buffer. Drain the complete message without
        // acknowledging it so the producer disconnect happens while the next frame waits.
        WebSocketReceiveResult initial;
        do
        {
            initial = await browser.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Binary, initial.MessageType);
        }
        while (!initial.EndOfMessage);

        await host.Presentation.DisposeAsync();

        try
        {
            var closed = await browser.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        }
        catch (WebSocketException ex)
        {
            // Cancelling the server's pending ReceiveAsync can abort the socket.
            // Either close path must end promptly, without waiting for the HWT ACK timeout.
            Assert.Equal(WebSocketError.ConnectionClosedPrematurely, ex.WebSocketErrorCode);
        }
    }

    private static Task SendAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        return socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReadUntilAsync(WebSocket socket, Func<JsonElement, bool> predicate, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // HWT1: four-byte magic, little-endian JSON byte length, JSON metadata,
            // then binary cell/image sections. Inspect only metadata in these transport tests.
            var bytes = message.ToArray();
            Assert.Equal("HWT1", Encoding.ASCII.GetString(bytes, 0, 4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
            using var document = JsonDocument.Parse(bytes.AsMemory(8, length));
            var frame = document.RootElement;
            await SendAsync(socket, $$"""{"type":"ack","revision":{{frame.GetProperty("revision").GetUInt32()}}}""", cancellationToken);
            if (predicate(frame))
            {
                return frame.Clone();
            }
        }
    }
}
