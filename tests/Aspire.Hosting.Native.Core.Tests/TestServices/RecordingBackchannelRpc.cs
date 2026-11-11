// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Rpc;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class RecordingBackchannelRpc(INativeBackchannelRpc inner) : INativeBackchannelRpc
{
    private readonly Channel<bool> _connections = Channel.CreateUnbounded<bool>();
    public ConcurrentQueue<string> Methods { get; } = new();

    public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
        _connections.Reader.ReadAsync(cancellationToken).AsTask();

    public Task ServeAsync(Stream stream,
        Func<NativeCliRequest, CancellationToken, Task<NativeBackchannelReply>> dispatch,
        CancellationToken cancellationToken)
    {
        var serving = inner.ServeAsync(stream, (request, token) =>
        {
            Methods.Enqueue(request.Method);
            return dispatch(request, token);
        }, cancellationToken);
        _connections.Writer.TryWrite(true);

        return serving;
    }
}
