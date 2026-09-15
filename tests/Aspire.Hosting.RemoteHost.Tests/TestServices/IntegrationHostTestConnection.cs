// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Tests;

internal sealed class IntegrationHostTestConnection : IDisposable
{
    private readonly JsonRpc _hostRpc;

    public IntegrationHostTestConnection(JsonElement capabilities)
    {
        var (serverStream, hostStream) = FullDuplexStream.CreatePair();
        ServerRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new SystemTextJsonFormatter()));
        _hostRpc = new JsonRpc(new HeaderDelimitedMessageHandler(hostStream, hostStream, new SystemTextJsonFormatter()));
        _hostRpc.AddLocalRpcMethod("getCapabilities", (Func<JsonElement>)(() => capabilities));
        _hostRpc.StartListening();
        ServerRpc.StartListening();
    }

    public JsonRpc ServerRpc { get; }

    public void Dispose()
    {
        ServerRpc.Dispose();
        _hostRpc.Dispose();
    }
}
