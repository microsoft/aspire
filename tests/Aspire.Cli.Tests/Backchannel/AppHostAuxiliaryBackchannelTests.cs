// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Backchannel;

public class AppHostAuxiliaryBackchannelTests
{
    [Fact]
    public async Task GetResourceSnapshotsAsync_SendsClientCapabilities()
    {
        using var server = TestAppHostBackchannelServer.Start();
        using var backchannel = await server.ConnectAsync().DefaultTimeout();

        var snapshots = await backchannel.GetResourceSnapshotsAsync(includeHidden: true).DefaultTimeout();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("api", snapshot.Name);
        Assert.NotNull(server.Target.GetResourcesRequest);
        Assert.Contains(AuxiliaryBackchannelCapabilities.V3, server.Target.GetResourcesRequest.ClientCapabilities);
        Assert.Contains(AuxiliaryBackchannelCapabilities.ResourceSnapshotVersions_V1, server.Target.GetResourcesRequest.ClientCapabilities);
    }

    [Fact]
    public async Task WatchResourceSnapshotsAsync_SendsClientCapabilities()
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
        Assert.Contains(AuxiliaryBackchannelCapabilities.ResourceSnapshotVersions_V1, server.Target.WatchResourcesRequest.ClientCapabilities);
    }

    [Fact]
    public async Task ConnectAsync_WhenResourceSnapshotVersionsCapabilityAdvertised_SupportsVersions()
    {
        using var server = TestAppHostBackchannelServer.Start();
        using var backchannel = await server.ConnectAsync().DefaultTimeout();

        Assert.True(backchannel.SupportsResourceSnapshotVersionsV1);
    }

    [Fact]
    public async Task GetTerminalInfoAsync_WhenTerminalsCapabilityMissing_ReturnsUnavailableWithoutCallingRpc()
    {
        // Terminals_V1 is absent from the server capabilities. The
        // TestAppHostRpcTarget also deliberately exposes no GetTerminalInfoAsync
        // method, so if the production capability gate is ever removed the call
        // would route to JsonRpc and fail with RemoteMethodNotFoundException —
        // i.e. this test would fail loudly the right way.
        using var server = TestAppHostBackchannelServer.Start();
        using var backchannel = await server.ConnectAsync().DefaultTimeout();

        var response = await backchannel.GetTerminalInfoAsync("frontend").DefaultTimeout();

        Assert.False(response.IsAvailable);
        Assert.Null(response.Replicas);
    }

    [Fact]
    public async Task ListTerminalsAsync_WhenTerminalsCapabilityMissing_ReturnsEmptyWithoutCallingRpc()
    {
        // See GetTerminalInfoAsync_WhenTerminalsCapabilityMissing_*: the server
        // exposes no ListTerminalsAsync handler, so reaching the RPC would
        // throw RemoteMethodNotFoundException. The capability gate must
        // short-circuit before that happens.
        using var server = TestAppHostBackchannelServer.Start();
        using var backchannel = await server.ConnectAsync().DefaultTimeout();

        var response = await backchannel.ListTerminalsAsync().DefaultTimeout();

        Assert.NotNull(response.Terminals);
        Assert.Empty(response.Terminals);
    }

    [Theory]
    [InlineData(nameof(TestAppHostRpcTarget.GetAppHostInformationAsync))]
    [InlineData(nameof(TestAppHostRpcTarget.GetCapabilitiesAsync))]
    public async Task ConnectAsync_WhenHandshakeRpcStalls_TimesOutAndDisposesConnection(string stalledMethod)
    {
        using var server = TestAppHostBackchannelServer.Start(stalledMethod);

        var connectTask = server.ConnectAsync(TimeSpan.FromSeconds(3));
        await server.WaitForStalledMethodEntryAsync().DefaultTimeout();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask).DefaultTimeout();
        await server.WaitForClientDisconnectAsync().DefaultTimeout();
    }

    private sealed class TestAppHostBackchannelServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<IDisposable> _disposables = [];
        private readonly TaskCompletionSource _clientDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TestAppHostBackchannelServer(string? stalledMethod)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            Target = new TestAppHostRpcTarget(stalledMethod);
        }

        public TestAppHostRpcTarget Target { get; }

        public static TestAppHostBackchannelServer Start(string? stalledMethod = null)
        {
            var server = new TestAppHostBackchannelServer(stalledMethod);
            server._listener.Start();

            return server;
        }

        public Task<AppHostAuxiliaryBackchannel> ConnectAsync() => ConnectAsyncCore(handshakeTimeout: null);

        public Task<AppHostAuxiliaryBackchannel> ConnectAsync(TimeSpan handshakeTimeout) => ConnectAsyncCore(handshakeTimeout);

        private async Task<AppHostAuxiliaryBackchannel> ConnectAsyncCore(TimeSpan? handshakeTimeout)
        {
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var acceptTask = _listener.AcceptSocketAsync();
            await clientSocket.ConnectAsync((IPEndPoint)_listener.LocalEndpoint).DefaultTimeout();
            var serverSocket = await acceptTask.DefaultTimeout();
            var serverStream = new NetworkStream(serverSocket, ownsSocket: true);
            var messageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
            var rpc = new JsonRpc(messageHandler, Target);
            rpc.Disconnected += (_, _) => _clientDisconnected.TrySetResult();
            rpc.StartListening();
            _disposables.Add(rpc);
            _disposables.Add(messageHandler);
            _disposables.Add(serverStream);

            if (handshakeTimeout is { } timeout)
            {
                return await AppHostAuxiliaryBackchannel.CreateFromSocketAsync(new TestAppHostSocket("socket.hash1"), isInScope: true, NullLogger.Instance, new ProfilingTelemetry(new ConfigurationBuilder().Build()), clientSocket, timeout, CancellationToken.None).DefaultTimeout();
            }

            return await AppHostAuxiliaryBackchannel.CreateFromSocketAsync(new TestAppHostSocket("socket.hash1"), isInScope: true, NullLogger.Instance, new ProfilingTelemetry(new ConfigurationBuilder().Build()), clientSocket, CancellationToken.None).DefaultTimeout();
        }

        public Task WaitForClientDisconnectAsync() => _clientDisconnected.Task;

        public Task WaitForStalledMethodEntryAsync() => Target.WaitForStalledMethodEntryAsync();

        public void Dispose()
        {
            Target.ReleaseStall();

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
        private readonly string? _stalledMethod;
        private readonly TaskCompletionSource _stalledMethodEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string[] _capabilities =
        [
            AuxiliaryBackchannelCapabilities.V1,
            AuxiliaryBackchannelCapabilities.V2,
            AuxiliaryBackchannelCapabilities.ResourceSnapshotVersions_V1
        ];

        public TestAppHostRpcTarget(string? stalledMethod)
        {
            _stalledMethod = stalledMethod;
        }

        public GetResourcesRequest? GetResourcesRequest { get; private set; }

        public WatchResourcesRequest? WatchResourcesRequest { get; private set; }

        public async Task<AppHostInformation> GetAppHostInformationAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            await StallIfRequestedAsync(nameof(GetAppHostInformationAsync));

            return new AppHostInformation
            {
                AppHostPath = "/path/to/AppHost.csproj",
                ProcessId = _processId
            };
        }

        public async Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null, CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            await StallIfRequestedAsync(nameof(GetCapabilitiesAsync));

            return new GetCapabilitiesResponse
            {
                Capabilities = _capabilities
            };
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

        public void ReleaseStall() => _releaseStall.TrySetResult();

        public Task WaitForStalledMethodEntryAsync() => _stalledMethodEntered.Task;

        private Task StallIfRequestedAsync(string method)
        {
            if (_stalledMethod != method)
            {
                return Task.CompletedTask;
            }

            _stalledMethodEntered.TrySetResult();
            return _releaseStall.Task;
        }
    }
}
