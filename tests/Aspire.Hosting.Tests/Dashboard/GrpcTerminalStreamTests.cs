// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Tests.Utils.Grpc;
using Google.Protobuf;

namespace Aspire.Hosting.Tests.Dashboard;

public class GrpcTerminalStreamTests
{
    [Fact]
    public async Task ReadAsync_SplitsSingleFrameAcrossReads()
    {
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<TerminalClientFrame>(context);
        var responseStream = new TestServerStreamWriter<TerminalServerFrame>(context);
        await using var stream = new GrpcTerminalStream(requestStream, responseStream);

        requestStream.AddMessage(new TerminalClientFrame { Data = ByteString.CopyFrom("hello"u8.ToArray()) });

        var buffer = new byte[2];

        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("he"u8.ToArray(), buffer);

        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal("ll"u8.ToArray(), buffer);

        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal("o"u8.ToArray(), buffer[..1]);
    }

    [Fact]
    public async Task ReadAsync_SkipsEmptyFramesWithoutSignallingEndOfStream()
    {
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<TerminalClientFrame>(context);
        var responseStream = new TestServerStreamWriter<TerminalServerFrame>(context);
        await using var stream = new GrpcTerminalStream(requestStream, responseStream);

        requestStream.AddMessage(new TerminalClientFrame());
        requestStream.AddMessage(new TerminalClientFrame { Data = ByteString.CopyFrom("x"u8.ToArray()) });

        var buffer = new byte[8];

        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal((byte)'x', buffer[0]);
    }

    [Fact]
    public async Task ReadAsync_ReturnsZeroWhenRequestStreamCompletes()
    {
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<TerminalClientFrame>(context);
        var responseStream = new TestServerStreamWriter<TerminalServerFrame>(context);
        await using var stream = new GrpcTerminalStream(requestStream, responseStream);

        requestStream.Complete();

        Assert.Equal(0, await stream.ReadAsync(new byte[8]));
        // A second read must stay at end of stream rather than pulling on the completed reader again.
        Assert.Equal(0, await stream.ReadAsync(new byte[8]));
    }

    [Fact]
    public async Task WriteAsync_CopiesBufferSoCallerCanReuseIt()
    {
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<TerminalClientFrame>(context);
        var responseStream = new TestServerStreamWriter<TerminalServerFrame>(context);
        await using var stream = new GrpcTerminalStream(requestStream, responseStream);

        var buffer = "ok"u8.ToArray();
        await stream.WriteAsync(buffer);
        buffer[0] = (byte)'X';

        var frame = await responseStream.ReadNextAsync();
        Assert.Equal("ok", frame.Data.ToStringUtf8());
    }

    [Fact]
    public async Task WriteAsync_EmptyBufferDoesNotProduceFrame()
    {
        var context = TestServerCallContext.Create();
        var requestStream = new TestAsyncStreamReader<TerminalClientFrame>(context);
        var responseStream = new TestServerStreamWriter<TerminalServerFrame>(context);
        await using var stream = new GrpcTerminalStream(requestStream, responseStream);

        await stream.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await stream.WriteAsync("data"u8.ToArray());

        var frame = await responseStream.ReadNextAsync();
        Assert.Equal("data", frame.Data.ToStringUtf8());
    }
}
