// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Dashboard;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Dashboard;

[Trait("Partition", "3")]
public class ResourcePublisherTests
{
    [Fact]
    public async Task ProducesExpectedSnapshotAndUpdates()
    {
        CancellationTokenSource cts = new();
        ResourcePublisher publisher = new(cts.Token);

        var a = CreateResourceSnapshot("A");
        var b = CreateResourceSnapshot("B");
        var c = CreateResourceSnapshot("C");

        await publisher.IntegrateAsync(new TestResource("A"), a, ResourceSnapshotChangeType.Upsert).DefaultTimeout();
        await publisher.IntegrateAsync(new TestResource("B"), b, ResourceSnapshotChangeType.Upsert).DefaultTimeout();

        Assert.Equal(0, publisher.OutgoingSubscriberCount);

        var (snapshot, subscription) = publisher.Subscribe();

        Assert.Equal(1, publisher.OutgoingSubscriberCount);

        Assert.Equal(2, snapshot.Length);
        Assert.Single(snapshot, s => s.Name == "A");
        Assert.Single(snapshot, s => s.Name == "B");

        var tcs = new TaskCompletionSource<IReadOnlyList<ResourceSnapshotChange>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = Task.Run(async () =>
        {
            await foreach (var change in subscription)
            {
                tcs.TrySetResult(change);
            }
        });

        await publisher.IntegrateAsync(new TestResource("C"), c, ResourceSnapshotChangeType.Upsert).DefaultTimeout();

        var change = Assert.Single(await tcs.Task.DefaultTimeout());
        Assert.Equal(ResourceSnapshotChangeType.Upsert, change.ChangeType);
        Assert.Equal("C", change.Resource.Name);

        await cts.CancelAsync().DefaultTimeout();

        try
        {
            await task.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
            // Ignore possible cancellation error.
        }

        Assert.Equal(0, publisher.OutgoingSubscriberCount);
    }

    [Fact]
    public async Task SupportsMultipleSubscribers()
    {
        CancellationTokenSource cts = new();
        ResourcePublisher publisher = new(cts.Token);

        var a = CreateResourceSnapshot("A");
        var b = CreateResourceSnapshot("B");
        var c = CreateResourceSnapshot("C");

        await publisher.IntegrateAsync(new TestResource("A"), a, ResourceSnapshotChangeType.Upsert).DefaultTimeout();
        await publisher.IntegrateAsync(new TestResource("B"), b, ResourceSnapshotChangeType.Upsert).DefaultTimeout();

        Assert.Equal(0, publisher.OutgoingSubscriberCount);

        var (snapshot1, subscription1) = publisher.Subscribe();
        var (snapshot2, subscription2) = publisher.Subscribe();

        Assert.Equal(2, publisher.OutgoingSubscriberCount);

        Assert.Equal(2, snapshot1.Length);
        Assert.Equal(2, snapshot2.Length);

        await publisher.IntegrateAsync(new TestResource("C"), c, ResourceSnapshotChangeType.Upsert).DefaultTimeout();

        var enumerator1 = subscription1.GetAsyncEnumerator(cts.Token);
        var enumerator2 = subscription2.GetAsyncEnumerator(cts.Token);

        await enumerator1.MoveNextAsync().DefaultTimeout();
        await enumerator2.MoveNextAsync().DefaultTimeout();

        var v1 = Assert.Single(enumerator1.Current);
        var v2 = Assert.Single(enumerator2.Current);

        Assert.Equal(ResourceSnapshotChangeType.Upsert, v1.ChangeType);
        Assert.Equal(ResourceSnapshotChangeType.Upsert, v2.ChangeType);
        Assert.Equal("C", v1.Resource.Name);
        Assert.Equal("C", v2.Resource.Name);

        await cts.CancelAsync().DefaultTimeout();

        Assert.False(await enumerator1.MoveNextAsync().DefaultTimeout());
        Assert.False(await enumerator2.MoveNextAsync().DefaultTimeout());

        Assert.Equal(0, publisher.OutgoingSubscriberCount);
    }

    [Fact]
    public async Task MergesResourcesInSnapshot()
    {
        CancellationTokenSource cts = new();
        ResourcePublisher publisher = new(cts.Token);

        var a1 = CreateResourceSnapshot("A");
        var a2 = CreateResourceSnapshot("A");
        var a3 = CreateResourceSnapshot("A");

        await publisher.IntegrateAsync(new TestResource("A"), a1, ResourceSnapshotChangeType.Upsert).DefaultTimeout();
        await publisher.IntegrateAsync(new TestResource("A"), a2, ResourceSnapshotChangeType.Upsert).DefaultTimeout();
        await publisher.IntegrateAsync(new TestResource("A"), a3, ResourceSnapshotChangeType.Upsert).DefaultTimeout();

        var (snapshot, _) = publisher.Subscribe();

        Assert.Equal("A", Assert.Single(snapshot).Name);

        await cts.CancelAsync().DefaultTimeout();
    }

    [Fact]
    public async Task DeletesRemoveFromSnapshot()
    {
        CancellationTokenSource cts = new();
        ResourcePublisher publisher = new(cts.Token);

        var a = CreateResourceSnapshot("A");
        var b = CreateResourceSnapshot("B");

        await publisher.IntegrateAsync(new TestResource("A"), a, ResourceSnapshotChangeType.Upsert).DefaultTimeout();
        await publisher.IntegrateAsync(new TestResource("B"), b, ResourceSnapshotChangeType.Upsert).DefaultTimeout();
        await publisher.IntegrateAsync(new TestResource("A"), a, ResourceSnapshotChangeType.Delete).DefaultTimeout();

        var (snapshot, _) = publisher.Subscribe();

        Assert.Equal("B", Assert.Single(snapshot).Name);

        await cts.CancelAsync().DefaultTimeout();
    }

    [Fact]
    public async Task CancelledSubscriptionIsCleanedUp()
    {
        ResourcePublisher publisher = new(CancellationToken.None);
        CancellationTokenSource cts = new();
        var called = false;

        var (_, subscription) = publisher.Subscribe();

        var task = Task.Run(async () =>
        {
            await foreach (var item in subscription.WithCancellation(cts.Token).ConfigureAwait(false))
            {
                // We should only loop one time.
                Assert.False(called);
                called = true;

                // Now we've received something, cancel.
                await cts.CancelAsync().DefaultTimeout();
            }
        });

        // Push through an update.
        await publisher.IntegrateAsync(new TestResource("A"), CreateResourceSnapshot("A"), ResourceSnapshotChangeType.Upsert).DefaultTimeout();

        // Let the subscriber exit.
        await task.DefaultTimeout();
    }

    private static GenericResourceSnapshot CreateResourceSnapshot(string name)
    {
        return new GenericResourceSnapshot(new()
        {
            Properties = [],
            ResourceType = KnownResourceTypes.Container
        })
        {
            Name = name,
            Uid = "",
            State = null,
            StateStyle = null,
            ExitCode = null,
            CreationTimeStamp = null,
            StartTimeStamp = null,
            StopTimeStamp = null,
            DisplayName = "",
            Urls = [],
            Volumes = [],
            Environment = [],
            HealthReports = [],
            Commands = [],
            Relationships = [],
            IsHidden = false,
            SupportsDetailedTelemetry = false,
            IconName = null,
            IconVariant = null
        };
    }

    private sealed class TestResource(string name) : Resource(name)
    {
    }
}
