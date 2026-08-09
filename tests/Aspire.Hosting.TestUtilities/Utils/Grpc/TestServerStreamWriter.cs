// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Grpc.Core;

namespace Aspire.Hosting.Tests.Utils.Grpc;

public class TestServerStreamWriter<T> : IServerStreamWriter<T> where T : class
{
    private readonly ServerCallContext _serverCallContext;
    private readonly Channel<T> _channel;

    public WriteOptions? WriteOptions { get; set; }

    public TestServerStreamWriter(ServerCallContext serverCallContext)
    {
        _channel = Channel.CreateUnbounded<T>();

        _serverCallContext = serverCallContext;
    }

    public void Complete(Exception? ex = null)
    {
        _channel.Writer.Complete(ex);
    }

    public IAsyncEnumerable<T> ReadAllAsync()
    {
        return _channel.Reader.ReadAllAsync();
    }

    public async Task<T> ReadNextAsync()
    {
        if (await _channel.Reader.WaitToReadAsync())
        {
            _channel.Reader.TryRead(out var message);
            return message!;
        }

        throw new InvalidOperationException("Unable to read message.");
    }

    public Task WriteAsync(T message, CancellationToken cancellationToken)
    {
        if (_serverCallContext.CancellationToken.IsCancellationRequested ||
            _serverCallContext.CancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(_serverCallContext.CancellationToken);
        }

        if (!_channel.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("Unable to write message.");
        }

        return Task.CompletedTask;
    }

    public Task WriteAsync(T message)
    {
        return WriteAsync(message, CancellationToken.None);
    }
}
