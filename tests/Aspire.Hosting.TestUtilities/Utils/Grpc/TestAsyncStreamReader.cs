// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Grpc.Core;

namespace Aspire.Hosting.Tests.Utils.Grpc;

public class TestAsyncStreamReader<T> : IAsyncStreamReader<T> where T : class
{
    private readonly Channel<T> _channel;
    private readonly ServerCallContext _serverCallContext;

    public T Current { get; private set; } = null!;

    public TestAsyncStreamReader(ServerCallContext serverCallContext)
    {
        _channel = Channel.CreateUnbounded<T>();
        _serverCallContext = serverCallContext;
    }

    public void AddMessage(T message)
    {
        if (!_channel.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("Unable to write message.");
        }
    }

    public void Complete(Exception? ex = null)
    {
        _channel.Writer.Complete(ex);
    }

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        _serverCallContext.CancellationToken.ThrowIfCancellationRequested();

        if (await _channel.Reader.WaitToReadAsync(cancellationToken) &&
            _channel.Reader.TryRead(out var message))
        {
            Current = message;
            return true;
        }
        else
        {
            Current = null!;
            return false;
        }
    }
}
