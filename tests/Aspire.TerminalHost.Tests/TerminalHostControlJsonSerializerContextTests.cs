// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;
using Microsoft.AspNetCore.InternalTesting;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Aspire.TerminalHost.Tests;

public class TerminalHostControlJsonSerializerContextTests
{
    [Fact]
    public void RpcMessageFormatterDoesNotFallBackToReflection()
    {
        var formatter = TerminalHostControlJsonSerializerContext.CreateRpcMessageFormatter();

        Assert.Throws<NotSupportedException>(() => formatter.JsonSerializerOptions.GetTypeInfo(typeof(Uri)));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    public async Task ControlProtocolRoundTrips(bool sourceGeneratedClient, bool sourceGeneratedServer, bool includeOptionalFields)
    {
        var expectedSession = new TerminalHostSessionInfo
        {
            ProducerUdsPath = "producer.sock",
            ConsumerUdsPath = "consumer.sock",
            IsAlive = true,
            ProducerConnected = true,
            RestartCount = 3,
            ExitCode = includeOptionalFields ? 7 : null,
            CurrentColumns = includeOptionalFields ? 120 : null,
            CurrentRows = includeOptionalFields ? 42 : null,
            AttachedPeerCount = includeOptionalFields ? 2 : null,
            Peers = includeOptionalFields
                ? [
                    new TerminalHostPeerInfo { PeerId = "peer-a", DisplayName = "aspire-cli:1234" },
                    new TerminalHostPeerInfo { PeerId = "peer-b", DisplayName = null }
                ]
                : null
        };
        var expectedInfo = new TerminalHostInfoResponse
        {
            ProtocolVersion = TerminalHostControlProtocol.ProtocolVersion
        };

        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, CreateFormatter(sourceGeneratedClient)));
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, CreateFormatter(sourceGeneratedServer)));
        var shutdownRequested = false;
        server.AddLocalRpcMethod(TerminalHostControlProtocol.GetSessionMethod, (Func<TerminalHostSessionInfo>)(() => expectedSession));
        server.AddLocalRpcMethod(TerminalHostControlProtocol.GetInfoMethod, (Func<TerminalHostInfoResponse>)(() => expectedInfo));
        server.AddLocalRpcMethod(TerminalHostControlProtocol.ShutdownMethod, (Action)(() => shutdownRequested = true));
        server.StartListening();
        client.StartListening();

        var session = await client.InvokeAsync<TerminalHostSessionInfo>(TerminalHostControlProtocol.GetSessionMethod).DefaultTimeout();
        var info = await client.InvokeAsync<TerminalHostInfoResponse>(TerminalHostControlProtocol.GetInfoMethod).DefaultTimeout();
        await client.InvokeAsync(TerminalHostControlProtocol.ShutdownMethod).DefaultTimeout();

        Assert.Equivalent(expectedSession, session, strict: true);
        Assert.Equivalent(expectedInfo, info, strict: true);
        Assert.True(shutdownRequested);
    }

    [Fact]
    public async Task RpcErrorsRoundTrip()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream, clientStream, TerminalHostControlJsonSerializerContext.CreateRpcMessageFormatter()));
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(
            serverStream, serverStream, TerminalHostControlJsonSerializerContext.CreateRpcMessageFormatter()));
        server.AddLocalRpcMethod("fail", (Action)(() => throw new InvalidOperationException("Control request failed.")));
        server.StartListening();
        client.StartListening();

        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => client.InvokeAsync("fail").DefaultTimeout());

        Assert.Equal("Control request failed.", exception.Message);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => client.InvokeAsync("unknown").DefaultTimeout());
    }

    [Fact]
    public async Task CancellationReachesServer()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream, clientStream, TerminalHostControlJsonSerializerContext.CreateRpcMessageFormatter()));
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(
            serverStream, serverStream, TerminalHostControlJsonSerializerContext.CreateRpcMessageFormatter()));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("wait", (Func<CancellationToken, Task>)(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        server.AddLocalRpcMethod("ping", (Func<bool>)(() => true));
        server.StartListening();
        client.StartListening();

        using var cts = new CancellationTokenSource();
        var invocation = client.InvokeWithCancellationAsync("wait", arguments: null, cts.Token);
        await started.Task.DefaultTimeout();
        await cts.CancelAsync();

        await cancelled.Task.DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.DefaultTimeout());
        Assert.True(await client.InvokeAsync<bool>("ping").DefaultTimeout());
    }

    private static SystemTextJsonFormatter CreateFormatter(bool sourceGenerated)
    {
        return sourceGenerated
            ? TerminalHostControlJsonSerializerContext.CreateRpcMessageFormatter()
            : new SystemTextJsonFormatter();
    }
}
