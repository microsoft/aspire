// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Native.Rpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeFramingTests
{
    [Fact]
    public async Task CoalescedFramesPreserveUtf8ByteLengthsAndTheNextMessage()
    {
        using var stream = new MemoryStream();
        await NativeRpcFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("{\"message\":\"caf\u00e9\"}"), CancellationToken.None);
        await NativeRpcFraming.WriteAsync(stream, "{}"u8.ToArray(), CancellationToken.None);
        stream.Position = 0;
        var reader = new NativeRpcFraming(stream);
        Assert.Equal("{\"message\":\"caf\u00e9\"}", Encoding.UTF8.GetString((await reader.ReadAsync(CancellationToken.None))!));
        Assert.Equal("{}", Encoding.UTF8.GetString((await reader.ReadAsync(CancellationToken.None))!));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: 262145\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 1\r\n\r\n")]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: invalid\r\n\r\n")]
    [InlineData("Content-Type: application/json\r\n\r\n")]
    public async Task InvalidLengthsAreRejectedBeforeReadingThePayload(string header)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(header));
        var reader = new NativeRpcFraming(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: 10\r\n")]
    [InlineData("Content-Length: 10\r\n\r\n{}")]
    public async Task TruncatedFramesAreNotTreatedAsCompletedRequests(string frame)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(frame));
        var reader = new NativeRpcFraming(stream);
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None));
    }
}
