// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Auxiliary;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class NativeAuxiliaryTestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeAuxiliaryBackchannel _protocol;
    private readonly List<JsonRpc> _clients = [];
    private readonly Task _run;
    public string HomeDirectory { get; }
    public string AppHostPath { get; }
    public string SocketPath => _protocol.SocketPath;
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int? ExitCode { get; private set; }
    public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public NativeCliLogBuffer Logs { get; } = new(10);

    public NativeAuxiliaryTestHost(NativeApplicationServer server) : this(server, new NativeServerOptions())
    {
    }

    public NativeAuxiliaryTestHost(NativeApplicationServer server, NativeServerOptions options)
        : this(server, options, new NativeBackchannelRpc(options, NullLogger.Instance))
    {
    }

    public NativeAuxiliaryTestHost(NativeApplicationServer server, NativeServerOptions options, INativeBackchannelRpc rpc)
    {
        // MTP starts in the deeply nested test output directory. Allocate under the repository
        // instead so the shared compact socket still fits macOS's 104-byte AF_UNIX limit.
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Aspire.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        }
        HomeDirectory = Path.Combine(root.FullName, "artifacts", "aux", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(HomeDirectory);
        AppHostPath = Path.Combine(HomeDirectory, "apphost.mts");
        _protocol = new(AppHostPath, HomeDirectory, server, options, code =>
        {
            ExitCode = code;
            Stopped.TrySetResult();
            _lifetime.Cancel();
        }, Ready.Task.WaitAsync, _ => Task.FromResult(new NativeCliDashboardUrls()), () => null, Logs, new(null, null, null, null),
            rpc, NullLogger.Instance);
        _run = _protocol.RunAsync(_lifetime.Token);
    }

    public JsonRpc Connect()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        var stream = new NetworkStream(socket, ownsSocket: true);
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            }
        };
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
        rpc.StartListening();
        _clients.Add(rpc);

        return rpc;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        await _lifetime.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _protocol.Dispose();
            _lifetime.Dispose();
            Directory.Delete(HomeDirectory, recursive: true);
        }
    }
}
