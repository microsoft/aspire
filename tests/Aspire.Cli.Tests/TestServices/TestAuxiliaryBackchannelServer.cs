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
    private JsonRpc? _rpc;

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
        var stream = new NetworkStream(socket, ownsSocket: true);
        var handler = new HeaderDelimitedMessageHandler(stream, stream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
        _rpc = new JsonRpc(handler, _target);
        _rpc.StartListening();
    }

    public void RemoveSocketFile() => File.Delete(_socketPath);

    public void Dispose()
    {
        _rpc?.Dispose();
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
