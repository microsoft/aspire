// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Core.Tests;

public class DcpResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedStreamAccountsFragmentedSynchronousAndAsynchronousReads(bool asynchronous)
    {
        using var input = new DcpResponseStream(new MemoryStream([1, 2, 3]), 3);
        var buffer = new byte[2];
        Assert.Equal(2, asynchronous ? await input.ReadAsync(buffer, TestContext.Current.CancellationToken) : input.Read(buffer));
        Assert.Equal(1, asynchronous ? await input.ReadAsync(buffer, TestContext.Current.CancellationToken) : input.Read(buffer));
        Assert.Equal(0, asynchronous ? await input.ReadAsync(buffer, TestContext.Current.CancellationToken) : input.Read(buffer));
        using var overflow = new DcpResponseStream(new MemoryStream([1, 2, 3, 4]), 3);
        Assert.Equal(2, asynchronous ? await overflow.ReadAsync(buffer, TestContext.Current.CancellationToken) : overflow.Read(buffer));
        if (asynchronous)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                Assert.Equal(2, await overflow.ReadAsync(buffer, TestContext.Current.CancellationToken)));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => overflow.Read(buffer));
        }
    }

    [Fact]
    public async Task ResourceResponsesDeserializeDirectlyAndIgnoreUnconsumedMetadata()
    {
        using var handler = Responses("""{"metadata":{"annotations":{"unused":"text"}},"status":{"state":"Running","pid":42}}""");
        await using var client = handler.CreateClient(new());
        var resource = await client.GetAsync(DcpResourceCollection.Executables, "job", TestContext.Current.CancellationToken);
        Assert.Equal(new DcpStatus { State = DcpProcessState.Running, Pid = 42 }, resource!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseLimitAcceptsExactBudgetAndRejectsTheNextByte(bool logs)
    {
        var limit = logs ? 65536 : 1024 * 1024;
        var exact = logs ? new string('x', limit) : "{}" + new string(' ', limit - 2);
        using var handler = Responses(exact);
        await using var client = handler.CreateClient(new());
        if (logs)
        {
            Assert.Empty(await client.ReadLogsAsync(DcpResourceCollection.Executables, "job", LogStream.Stdout, 0, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(new DcpResource(null), await client.GetAsync(DcpResourceCollection.Executables, "job", TestContext.Current.CancellationToken));
        }
        handler.ResponseFactory = _ => Json(exact + "x");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (logs)
            {
                await client.ReadLogsAsync(DcpResourceCollection.Executables, "job", LogStream.Stdout, 0, TestContext.Current.CancellationToken);
            }
            else
            {
                await client.GetAsync(DcpResourceCollection.Executables, "job", TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public async Task DirectJsonDeserializationKeepsTheExistingDepthLimit()
    {
        var deep = "{\"unused\":" + new string('[', 33) + "0" + new string(']', 33) + "}";
        using var handler = Responses(deep);
        await using var client = handler.CreateClient(new());
        await Assert.ThrowsAsync<JsonException>(() =>
            client.GetAsync(DcpResourceCollection.Executables, "job", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogDecodingPreservesUtf8BomAndExcludesTheIncompleteTail()
    {
        using var handler = Responses("\uFEFFr\u00E9ady\r\n\n\u65E5\u672C\u8A9E\npartial");
        await using var client = handler.CreateClient(new());
        Assert.Equal(["\uFEFFr\u00E9ady\r", "", "\u65E5\u672C\u8A9E"],
            await client.ReadLogsAsync(DcpResourceCollection.Executables, "job", LogStream.Stdout, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogDecodingHandlesAMultibyteCharacterAcrossPooledSegments()
    {
        var line = new string('x', 4095) + "\u65E5";
        using var handler = Responses(line + "\npartial");
        await using var client = handler.CreateClient(new());
        Assert.Equal([line], await client.ReadLogsAsync(DcpResourceCollection.Executables, "job", LogStream.Stdout, 0,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAcceptsKubernetesStatusWithoutInterpretingItAsWorkloadStatus()
    {
        using var handler = new DcpCleanupTestHandler
        {
            ResponseFactory = request => request.Method == HttpMethod.Delete
                ? Json("""{"kind":"Status","status":"Success"}""") : new(HttpStatusCode.NotFound)
        };
        await using var client = handler.CreateClient(new());
        await client.DeleteAsync(DcpResourceCollection.Executables, "job", TestContext.Current.CancellationToken);
    }

    private static DcpCleanupTestHandler Responses(string body) => new() { ResponseFactory = _ => Json(body) };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, new UTF8Encoding(false), "application/json")
    };
}
