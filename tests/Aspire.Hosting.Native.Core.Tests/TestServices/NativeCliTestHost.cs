// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Server;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Connects the real StreamJsonRpc client to the native server's existing CLI protocol.</summary>
internal sealed class NativeCliTestHost : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("native-cli-test-");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeCliProtocol _protocol;
    private readonly Task _run;
    public NativeCliLogBuffer Logs { get; }
    public bool IsReady => _protocol.IsAppHostReady;
    public JsonRpc Rpc { get; }

    private NativeCliTestHost(NativeApplicationServer server, NativeServerOptions options,
        Func<NativeCliDashboardUrls> dashboard)
    {
        Logs = new NativeCliLogBuffer(options.RetainedAppHostLogEntries);
        var path = Path.Combine(_directory.FullName, "cli.sock");
        _protocol = new(path, server, _lifetime.Cancel, _ => Task.FromResult(dashboard()), Logs, options);
        _run = _protocol.RunAsync(_lifetime.Token);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        var stream = new NetworkStream(socket, ownsSocket: true);
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        };
        Rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
        Rpc.StartListening();
    }

    public static NativeCliTestHost Start(NativeApplicationServer server, NativeServerOptions options,
        Func<NativeCliDashboardUrls> dashboard) => new(server, options, dashboard);

    public async ValueTask DisposeAsync()
    {
        Rpc.Dispose();
        await _lifetime.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _protocol.Dispose();
            _lifetime.Dispose();
            _directory.Delete(recursive: true);
        }
    }
}
