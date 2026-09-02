// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Backchannel;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAppHostSocket(string socketPath) : IAppHostSocket
{
    public string SocketPath { get; } = socketPath;

    public int? ProcessId { get; init; } = BackchannelConstants.ExtractPid(socketPath);

    public Func<CancellationToken, ValueTask<Socket>>? ConnectAsyncCallback { get; init; }

    public async ValueTask<Socket> ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectAsyncCallback is not null)
        {
            return await ConnectAsyncCallback(cancellationToken);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public bool TryDelete()
    {
        if (!File.Exists(SocketPath))
        {
            return false;
        }

        File.Delete(SocketPath);
        return true;
    }
}
