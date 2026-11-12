// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeDiagnosticChannelTests
{
    [Fact]
    public async Task SlowSubscribersOverflowIndependentlyAndDrainTheirRetainedPrefix()
    {
        using var source = new OperationEventSource();
        using var slow = source.Subscribe(capacity: 1);
        using var fast = source.Subscribe(capacity: 1);
        var first = Event(OperationOutcome.Started);
        var second = Event(OperationOutcome.Ok);
        source.Publish(first);
        Assert.Equal(first, await fast.Reader.ReadAsync(TestContext.Current.CancellationToken));
        source.Publish(second);
        Assert.Equal(second, await fast.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(first, await slow.Reader.ReadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DiagnosticOverflowException>(() => slow.Reader.Completion);
        source.Publish(first);
        Assert.Equal(first, await fast.Reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposalCompletesOnlyThatSubscriberAndSourceDisposalCompletesTheRest()
    {
        using var source = new OperationEventSource();
        using var first = source.Subscribe();
        using var second = source.Subscribe();
        var operation = Event(OperationOutcome.Ok);
        source.Publish(operation);
        first.Dispose();
        Assert.Equal(operation, await first.Reader.ReadAsync(TestContext.Current.CancellationToken));
        await first.Reader.Completion;
        Assert.Equal(operation, await second.Reader.ReadAsync(TestContext.Current.CancellationToken));
        source.Dispose();
        await second.Reader.Completion;
        Assert.Throws<ObjectDisposedException>(() => source.Subscribe());
        source.Publish(operation);
    }

    [Fact]
    public async Task AwaitingAChannelDoesNotRunAConsumerOnTheProducerStack()
    {
        using var source = new OperationEventSource();
        using var subscription = source.Subscribe();
        var awaited = subscription.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        var producerThread = Environment.CurrentManagedThreadId;
        var publishing = false;
        var inlineConsumer = false;
        var consumer = awaited.ContinueWith(_ =>
        {
            inlineConsumer = Volatile.Read(ref publishing) && Environment.CurrentManagedThreadId == producerThread;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var operation = Event(OperationOutcome.Error);
        Volatile.Write(ref publishing, true);
        source.Publish(operation);
        Volatile.Write(ref publishing, false);
        Assert.Equal(operation, await awaited);
        await consumer;
        Assert.False(inlineConsumer);
        subscription.Dispose();
        await subscription.Reader.Completion;
    }

    private static OperationEvent Event(OperationOutcome outcome) => new(
        Guid.NewGuid(), "test.operation", outcome, null, null, null, null, null,
        new FakeTimeProvider().GetUtcNow(), 0, null);
}
