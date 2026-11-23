// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Connections;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Runs real backchannel sockets without depending on a DCP process or a running AppHost.</summary>
internal sealed class NativeSocketTestHost : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _run;

    public NativeSocketTestHost(Func<NativeCliRequest, CancellationToken, Task<NativeBackchannelReply>> dispatch)
        : this((stream, token) => new NativeBackchannelRpc(new NativeServerOptions(), NullLogger.Instance)
            .ServeAsync(stream, dispatch, token), 4)
    {
    }

    public NativeSocketTestHost(Func<Stream, CancellationToken, Task> serve, int maximumConnections)
    {
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(maximumConnections);
        _run = SocketConnectionListener.RunAsync(_listener, maximumConnections, serve, _lifetime.Token);
    }

    public Task Completion => _run;
    public EndPoint EndPoint => _listener.LocalEndPoint!;

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _listener.Dispose();
            _lifetime.Dispose();
        }
    }
}

/// <summary>Exchanges framed JSON-RPC requests on an actual socket.</summary>
internal sealed class NativeSocketTestClient : IDisposable
{
    private readonly Stream _stream;
    private readonly NativeRpcFraming _framing;
    private int _id;
    private readonly Socket? _socket;
    public Socket Socket => _socket ?? throw new InvalidOperationException("This client uses a named pipe.");

    public NativeSocketTestClient(EndPoint endPoint)
    {
        if (endPoint is NamedPipeEndPoint pipe)
        {
            var stream = new NamedPipeClientStream(pipe.ServerName, pipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                stream.Connect(10000);
                _stream = stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        else
        {
            _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
            _socket.Connect(endPoint);
            _stream = new NetworkStream(_socket, ownsSocket: true);
        }
        // Bootstrap responses contain the complete offline-generated bundle and
        // can exceed the server's separate 256 KiB inbound request limit.
        _framing = new NativeRpcFraming(_stream, 4 * 1024 * 1024);
    }

    public Task SendRawAsync(string frame, CancellationToken cancellationToken) =>
        _stream.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken).AsTask();

    public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => _framing.ReadAsync(cancellationToken);

    public async Task<JsonObject> CallAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        await SendAsync(method, parameters, cancellationToken);
        var response = await ReadAsync(cancellationToken) ?? throw new EndOfStreamException("Expected a response.");
        return JsonNode.Parse(response)!.AsObject();
    }

    public Task SendAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters
        };
        return NativeRpcFraming.WriteAsync(_stream, Encoding.UTF8.GetBytes(request.ToJsonString()), cancellationToken);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Owns a dedicated socket directory beneath repository artifacts, including on Unix.</summary>
internal sealed class NativeSocketTestDirectory : IDisposable
{
    private readonly DirectoryInfo _directory;
    public string SocketPath => Path.Combine(_directory.FullName, "rpc.sock");

    public NativeSocketTestDirectory()
    {
        // MTP runs in artifacts/bin/<project>/<configuration>/<tfm>. Anchor
        // sockets at the repository to stay below Unix's 104-byte path limit.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Aspire.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("The repository root was not found.");
        }
        var path = Path.Combine(root.FullName, "artifacts", $"nst-{Guid.NewGuid():N}");
        _directory = OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(path)
            : Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
