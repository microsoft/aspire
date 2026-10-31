// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAuxiliaryBackchannelServer : IDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly string _socketPath;
    private readonly RpcTarget _target;
    private readonly TaskCompletionSource _clientDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonRpc? _rpc;
    private Socket? _connectionSocket;

    public TestAuxiliaryBackchannelServer(string socketPath, string appHostPath)
    {
        _socketPath = socketPath;
        _target = new(appHostPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(1);
    }

    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        var socket = await _listener.AcceptAsync(cancellationToken);
        // JsonRpc disposes its stream when the client disconnects. Keep the socket open until the test closes it.
        _connectionSocket = socket;
        var stream = new NetworkStream(socket, ownsSocket: false);
        var handler = new HeaderDelimitedMessageHandler(stream, stream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
        _rpc = new JsonRpc(handler, _target);
        _rpc.Disconnected += (_, _) =>
        {
            _clientDisconnected.TrySetResult();
            if (CloseOnClientDisconnect)
            {
                _ = Task.Run(CloseConnection);
            }
        };
        _rpc.StartListening();
    }

    /// <summary>
    /// Gets or sets whether the server closes its side as soon as the client disconnects, like a real AppHost.
    /// </summary>
    public bool CloseOnClientDisconnect { get; set; } = true;

    public Task WaitForClientDisconnectAsync() => _clientDisconnected.Task;

    /// <summary>
    /// Writes a JSON-RPC notification directly to the client. This succeeds when the client has only shut down
    /// its sending side and throws a <see cref="SocketException"/> when the client has closed its socket.
    /// </summary>
    public void SendNotificationToClient()
    {
        var body = """{"jsonrpc":"2.0","method":"testProbe"}"""u8;
        var header = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        _connectionSocket!.Send([.. header, .. body]);
    }

    public void CloseConnection()
    {
        _rpc?.Dispose();
        _connectionSocket?.Dispose();
    }

    public void RemoveSocketFile() => File.Delete(_socketPath);

    public void Dispose()
    {
        CloseConnection();
        _listener.Dispose();
        RemoveSocketFile();
    }

    private sealed class RpcTarget(string appHostPath)
    {
        private readonly string[] _capabilities = [AuxiliaryBackchannelCapabilities.V1];

        public Task<AppHostInformation> GetAppHostInformationAsync() => Task.FromResult(new AppHostInformation
        {
            AppHostPath = appHostPath,
            ProcessId = Environment.ProcessId
        });

        public Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null)
        {
            _ = request;
            return Task.FromResult(new GetCapabilitiesResponse { Capabilities = _capabilities });
        }
    }
}
