// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class RemoteAppHostServiceTests
{
    [Theory]
    [InlineData("canceledSync")]
    [InlineData("canceledTask")]
    [InlineData("canceledTaskWithResult")]
    [InlineData("canceledValueTask")]
    [InlineData("canceledValueTaskWithResult")]
    public async Task CanceledCapabilityPreservesRpcCancellationWithoutDisconnecting(string method)
    {
        var handles = new HandleRegistry();
        var tokens = new CancellationTokenRegistry();
        var context = new AtsContext { Capabilities = [], HandleTypes = [], DtoTypes = [], EnumTypes = [] };
        await using var invoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance);
        AtsCallbackProxyFactory? callbacks = null;
        var marshaller = new AtsMarshaller(handles, context, tokens, new Lazy<AtsCallbackProxyFactory>(() => callbacks!));
        callbacks = new AtsCallbackProxyFactory(invoker, handles, tokens, marshaller);
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(TestCapabilities).Assembly]);
        using var externalCapabilities = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var service = new RemoteAppHostService(
            new JsonRpcAuthenticationState(new ConfigurationBuilder().Build()),
            invoker, tokens, dispatcher, externalCapabilities,
            NullLogger<RemoteAppHostService>.Instance, RemoteHostProfilingTelemetry.Disabled);
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new SystemTextJsonFormatter()));
        using var clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, new SystemTextJsonFormatter()));
        serverRpc.AddLocalRpcTarget(service);
        service.SetClientConnection(serverRpc);
        serverRpc.StartListening();
        clientRpc.StartListening();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            clientRpc.InvokeAsync<JsonNode?>("invokeCapability", $"Aspire.Hosting.RemoteHost.Tests/{method}", new JsonObject())
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal("pong", await clientRpc.InvokeAsync<string>("ping")
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(invoker.LifetimeToken.IsCancellationRequested);
    }
}
