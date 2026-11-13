// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Tests;

internal sealed class IntegrationHostTestConnection : IDisposable
{
    private readonly JsonRpc _hostRpc;

    public IntegrationHostTestConnection(JsonElement capabilities)
        : this(_ => Task.FromResult(capabilities))
    {
    }

    public IntegrationHostTestConnection(JsonElement capabilities, Func<string, JsonObject?, Task<JsonNode?>> handleCapability)
        : this(_ => Task.FromResult(capabilities), handleCapability, null)
    {
    }

    public IntegrationHostTestConnection(JsonElement capabilities, Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> invokeCallback)
        : this(_ => Task.FromResult(capabilities), null, invokeCallback)
    {
    }

    public IntegrationHostTestConnection(Func<CancellationToken, Task<JsonElement>> getCapabilities)
        : this(getCapabilities, null, null)
    {
    }

    private IntegrationHostTestConnection(
        Func<CancellationToken, Task<JsonElement>> getCapabilities,
        Func<string, JsonObject?, Task<JsonNode?>>? handleCapability,
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>? invokeCallback)
    {
        var (serverStream, hostStream) = FullDuplexStream.CreatePair();
        ServerRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new SystemTextJsonFormatter()));
        CallbackInvoker.SetConnection(ServerRpc);
        _hostRpc = new JsonRpc(new HeaderDelimitedMessageHandler(hostStream, hostStream, new SystemTextJsonFormatter()));
        _hostRpc.SynchronizationContext = null;
        _hostRpc.AddLocalRpcMethod("getCapabilities", getCapabilities);
        if (handleCapability is not null)
        {
            _hostRpc.AddLocalRpcMethod("handleExternalCapability", handleCapability);
            _hostRpc.AddLocalRpcMethod("handleExternalCapability",
                (string capabilityId, JsonObject? args, string invocationId) =>
                {
                    LastInvocationId = invocationId;
                    return handleCapability(capabilityId, args);
                });
        }
        if (invokeCallback is not null)
        {
            _hostRpc.AddLocalRpcMethod("invokeCallback", invokeCallback);
        }
        _hostRpc.StartListening();
        ServerRpc.StartListening();
    }

    public JsonRpc ServerRpc { get; }
    public string? LastInvocationId { get; private set; }
    public JsonRpcCallbackInvoker CallbackInvoker { get; } = new(
        NullLogger<JsonRpcCallbackInvoker>.Instance,
        new Language.IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true"
        }).Build()), TimeProvider.System);

    public void Dispose()
    {
        ServerRpc.Dispose();
        _hostRpc.Dispose();
        CallbackInvoker.Dispose();
    }
}
