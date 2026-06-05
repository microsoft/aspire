// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Backchannel;

public class AppHostAuxiliaryBackchannelTests
{
    [Fact]
    public async Task GetResourceSnapshotsAsync_SendsClientCapabilitiesWithV3()
    {
        using var server = TestAppHostBackchannelServer.Start();
        using var backchannel = await server.ConnectAsync().DefaultTimeout();

        var snapshots = await backchannel.GetResourceSnapshotsAsync(includeHidden: true).DefaultTimeout();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("api", snapshot.Name);
        Assert.NotNull(server.Target.GetResourcesRequest);
        Assert.Contains(AuxiliaryBackchannelCapabilities.V3, server.Target.GetResourcesRequest.ClientCapabilities);
    }

    [Fact]
    public async Task WatchResourceSnapshotsAsync_SendsClientCapabilitiesWithV3()
    {
        using var server = TestAppHostBackchannelServer.Start();
        using var backchannel = await server.ConnectAsync().DefaultTimeout();

        using var watchCancellation = new CancellationTokenSource();
        await using var enumerator = backchannel.WatchResourceSnapshotsAsync(includeHidden: true, watchCancellation.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync().DefaultTimeout());
        await watchCancellation.CancelAsync();

        var resource = enumerator.Current;
        Assert.Equal("api", resource.Name);
        Assert.NotNull(server.Target.WatchResourcesRequest);
        Assert.Contains(AuxiliaryBackchannelCapabilities.V3, server.Target.WatchResourcesRequest.ClientCapabilities);
    }

    private sealed class TestAppHostBackchannelServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<IDisposable> _disposables = [];

        private TestAppHostBackchannelServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            Target = new TestAppHostRpcTarget();
        }

        public TestAppHostRpcTarget Target { get; }

        public static TestAppHostBackchannelServer Start()
        {
            var server = new TestAppHostBackchannelServer();
            server._listener.Start();

            return server;
        }

        public async Task<AppHostAuxiliaryBackchannel> ConnectAsync()
        {
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var acceptTask = _listener.AcceptSocketAsync();
            await clientSocket.ConnectAsync((IPEndPoint)_listener.LocalEndpoint).DefaultTimeout();
            var serverSocket = await acceptTask.DefaultTimeout();
            var serverStream = new NetworkStream(serverSocket, ownsSocket: true);
            var messageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
            var rpc = new JsonRpc(messageHandler, Target);
            rpc.StartListening();
            _disposables.Add(rpc);
            _disposables.Add(messageHandler);
            _disposables.Add(serverStream);

            return await AppHostAuxiliaryBackchannel.CreateFromSocketAsync("hash1", "socket.hash1", isInScope: true, NullLogger.Instance, clientSocket).DefaultTimeout();
        }

        public void Dispose()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            _listener.Stop();
        }
    }

    private sealed class TestAppHostRpcTarget
    {
        private readonly int _processId = Environment.ProcessId;
        private readonly string[] _capabilities =
        [
            AuxiliaryBackchannelCapabilities.V1,
            AuxiliaryBackchannelCapabilities.V2
        ];

        public GetResourcesRequest? GetResourcesRequest { get; private set; }

        public WatchResourcesRequest? WatchResourcesRequest { get; private set; }

        public Task<AppHostInformation> GetAppHostInformationAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;

            return Task.FromResult(new AppHostInformation
            {
                AppHostPath = "/path/to/AppHost.csproj",
                ProcessId = _processId
            });
        }

        public Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null, CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;

            return Task.FromResult(new GetCapabilitiesResponse
            {
                Capabilities = _capabilities
            });
        }

        public Task<GetResourcesResponse> GetResourcesAsync(GetResourcesRequest? request = null, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            GetResourcesRequest = request;

            return Task.FromResult(new GetResourcesResponse
            {
                Resources = [CreateResourceSnapshot()]
            });
        }

        public async IAsyncEnumerable<ResourceSnapshot> WatchResourcesAsync(WatchResourcesRequest? request = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WatchResourcesRequest = request;
            yield return CreateResourceSnapshot();
            await Task.CompletedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static ResourceSnapshot CreateResourceSnapshot() =>
            new()
            {
                Name = "api",
                ResourceType = "Project"
            };
    }
}
