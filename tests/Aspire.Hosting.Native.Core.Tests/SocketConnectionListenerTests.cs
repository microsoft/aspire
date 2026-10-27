// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Threading.Channels;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Transport;

namespace Aspire.Hosting.Native.Core.Tests;

public class SocketConnectionListenerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RejectsInvalidCapacityBeforeAccepting(int maximumConnections)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SocketConnectionListener.RunAsync(listener, maximumConnections, (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectionsRunIndependentlyAndShutdownDrainsAllHandlers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = Channel.CreateUnbounded<bool>();
        var drained = 0;
        await using (var host = new NativeSocketTestHost(async (stream, cancellationToken) =>
        {
            connected.Writer.TryWrite(true);
            try
            {
                var buffer = new byte[1];
                if (await stream.ReadAsync(buffer, cancellationToken) == 1)
                {
                    await stream.WriteAsync(buffer, cancellationToken);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref drained);
            }
        }, 2))
        {
            using var idle = new NativeSocketTestClient(host.EndPoint);
            await connected.Reader.ReadAsync(timeout.Token);
            using var active = new NativeSocketTestClient(host.EndPoint);
            await connected.Reader.ReadAsync(timeout.Token);
            await active.SendRawAsync("x", timeout.Token);
            var response = new byte[1];
            Assert.Equal(1, await active.Socket.ReceiveAsync(response, SocketFlags.None, timeout.Token));
            Assert.Equal((byte)'x', response[0]);
            Assert.False(host.Completion.IsCompleted);
        }
        Assert.Equal(2, drained);
    }

    [Fact]
    public async Task CapacityRejectsExcessConnectionsWithoutStoppingTheListener()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new NativeSocketTestHost((_, cancellationToken) =>
        {
            connected.TrySetResult();

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, 1);
        using var first = new NativeSocketTestClient(host.EndPoint);
        await connected.Task.WaitAsync(timeout.Token);
        using var excess = new NativeSocketTestClient(host.EndPoint);
        Assert.Equal(0, await excess.Socket.ReceiveAsync(new byte[1], SocketFlags.None, timeout.Token));
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task HandlerFailureCancelsAndDrainsOtherConnectionsWithoutWaitingForAnotherClient()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = Channel.CreateUnbounded<bool>();
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = 0;
        var drained = 0;
        var host = new NativeSocketTestHost(async (_, cancellationToken) =>
        {
            var index = Interlocked.Increment(ref accepted);
            connected.Writer.TryWrite(true);
            try
            {
                if (index == 1)
                {
                    await failure.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
            finally
            {
                Interlocked.Increment(ref drained);
            }
        }, 2);
        try
        {
            using var first = new NativeSocketTestClient(host.EndPoint);
            await connected.Reader.ReadAsync(timeout.Token);
            using var second = new NativeSocketTestClient(host.EndPoint);
            await connected.Reader.ReadAsync(timeout.Token);
            failure.SetException(new InvalidOperationException("test-handler-failure"));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Completion.WaitAsync(timeout.Token));
            Assert.Equal("test-handler-failure", exception.Message);
            Assert.Equal(2, drained);
        }
        finally
        {
            if (host.Completion.IsFaulted)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());
            }
            else
            {
                await host.DisposeAsync();
            }
        }
    }
}
