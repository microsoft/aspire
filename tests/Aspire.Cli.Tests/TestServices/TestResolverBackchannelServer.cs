// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A loopback auxiliary backchannel server that a resolver can connect to through
/// <see cref="AppHostSocket"/>, and that reports when the CLI side closes the connection.
/// </summary>
internal sealed class TestResolverBackchannelServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TestResolverRpcTarget _target;
    private readonly TaskCompletionSource _clientDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonRpc? _rpc;

    private TestResolverBackchannelServer(string appHostPath, int processId)
    {
        _target = new TestResolverRpcTarget(appHostPath, processId);
        _listener.Start();
        AppHostSocket = new TestAppHostSocket($"test-socket-{processId}")
        {
            ConnectAsyncCallback = ConnectAsync
        };
    }

    public TestAppHostSocket AppHostSocket { get; }

    public TestResolverRpcTarget Target => _target;

    public static TestResolverBackchannelServer Start(string appHostPath, int processId)
        => new(appHostPath, processId);

    public Task WaitForClientDisconnectAsync() => _clientDisconnected.Task;

    public void Dispose()
    {
        _rpc?.Dispose();
        _listener.Stop();
    }

    private async ValueTask<Socket> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync((IPEndPoint)_listener.LocalEndpoint, cancellationToken);
            var serverSocket = await _listener.AcceptSocketAsync(cancellationToken);
            var stream = new NetworkStream(serverSocket, ownsSocket: true);
            var messageHandler = new HeaderDelimitedMessageHandler(
                stream,
                stream,
                BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
            _rpc = new JsonRpc(messageHandler, _target);
            _rpc.Disconnected += (_, _) => _clientDisconnected.TrySetResult();
            _rpc.StartListening();
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class TestResolverRpcTarget(string appHostPath, int processId)
{
    private readonly string[] _capabilities =
    [
        AuxiliaryBackchannelCapabilities.V1,
        AuxiliaryBackchannelCapabilities.V2
    ];

    public Task<AppHostInformation> GetAppHostInformationAsync()
        => Task.FromResult(new AppHostInformation
        {
            AppHostPath = appHostPath,
            ProcessId = processId
        });

    public Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null)
    {
        _ = request;
        return Task.FromResult(new GetCapabilitiesResponse
        {
            Capabilities = _capabilities
        });
    }

    public Task<GetAppHostInfoResponse> GetAppHostInfoAsync(GetAppHostInfoRequest? request = null)
    {
        _ = request;
        return Task.FromResult(new GetAppHostInfoResponse
        {
            Pid = processId.ToString(CultureInfo.InvariantCulture),
            AppHostPath = appHostPath,
            AspireHostVersion = "test"
        });
    }

    public ResourceSnapshot[] Resources { get; set; } = [];

    public ConcurrentQueue<ExecuteResourceCommandRequest> ExecutedCommands { get; } = new();

    public Task<GetResourcesResponse> GetResourcesAsync(GetResourcesRequest? request = null)
    {
        _ = request;
        return Task.FromResult(new GetResourcesResponse { Resources = Resources });
    }

    public Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(ExecuteResourceCommandRequest request)
    {
        ExecutedCommands.Enqueue(request);
        return Task.FromResult(new ExecuteResourceCommandResponse { Success = true });
    }
}
