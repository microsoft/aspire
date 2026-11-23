// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Rpc;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeNamedPipeTests
{
    [Fact]
    public async Task AuthenticatesIndependentClientsAndDrainsConnectionsOnShutdown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new NativeRpcListener(CreateEndpoint(),
            Token, NullLogger.Instance);
        var run = listener.RunAsync(timeout.Token);
        try
        {
            using var first = new NativeSocketTestClient(listener.EndPoint);
            using var second = new NativeSocketTestClient(listener.EndPoint);
            var unauthenticated = await second.CallAsync("getApplicationServer", null, timeout.Token);
            Assert.NotNull(unauthenticated["error"]);
            Assert.True((await first.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            Assert.True((await second.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            Assert.NotNull((await first.CallAsync("getApplicationServer", null, timeout.Token))["result"]);
            Assert.NotNull((await second.CallAsync("getCapabilities", null, timeout.Token))["result"]);
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void RejectsRemotePipeEndpoints()
    {
        Assert.Throws<ArgumentException>(() => new NativeRpcListener(
            new NamedPipeEndPoint("aspire-v2-test", "remote-host"), Token, NullLogger.Instance));
    }

    [Fact]
    public void ExistingPipeCannotBeReplacedByAnotherListener()
    {
        var endpoint = CreateEndpoint();
        using var listener = new NativeRpcListener(endpoint, Token, NullLogger.Instance);
        Assert.Throws<UnauthorizedAccessException>(() => new NativeRpcListener(endpoint, Token, NullLogger.Instance));
    }

    // Unix emulates pipes as TMPDIR/CoreFxPipe_<name>. Keep names compact so
    // repository-local temp directories do not exceed the Unix socket path limit.
    private static NamedPipeEndPoint CreateEndpoint() => new($"v2-{Guid.NewGuid():N}"[..19]);
}
