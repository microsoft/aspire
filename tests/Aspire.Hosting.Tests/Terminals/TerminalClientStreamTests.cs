// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class TerminalClientStreamTests
{
    [Fact]
    public async Task DisposeAsync_WaitsForOutstandingIoAndDoesNotDisposeTransport()
    {
        var (transport, peer) = TestDuplexStream.CreatePair();
        using var transportOwner = transport;
        using var peerOwner = peer;
        using var gated = new GatedTerminalWriteStream(transport);
        await using var stream = new TerminalClientStream(gated);

        var write = stream.WriteAsync("hello"u8.ToArray()).AsTask();
        await gated.WriteStarted.DefaultTimeout();
        var read = stream.ReadAsync(new byte[1]).AsTask();

        var dispose = stream.DisposeAsync().AsTask();
        try
        {
            await gated.WriteCancelled.DefaultTimeout();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).DefaultTimeout();
            Assert.False(dispose.IsCompleted);
            Assert.False(stream.Released.IsCompleted);
            Assert.False(transport.Disposed);
            Assert.Same(dispose, stream.DisposeAsync().AsTask());
        }
        finally
        {
            gated.ReleaseWrite();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write).DefaultTimeout();
        await dispose.DefaultTimeout();
        Assert.False(transport.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync(new byte[1]).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[1]).AsTask());

        // The owner can still use or dispose the underlying transport after Hex1b releases its wrapper.
        await transport.WriteAsync("x"u8.ToArray());
        var buffer = new byte[1];
        Assert.Equal(1, await peer.ReadAsync(buffer).AsTask().DefaultTimeout());
        Assert.Equal((byte)'x', buffer[0]);
    }
}
