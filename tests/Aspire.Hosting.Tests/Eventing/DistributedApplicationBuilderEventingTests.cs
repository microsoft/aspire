// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.Eventing;

[Trait("Partition", "6")]
public class DistributedApplicationBuilderEventingTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task EventsCanBePublishedBlockSequential()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var hitCount = 0;
        var blockAssertionTcs = new TaskCompletionSource();
        var blockFirstSubscriptionTcs = new TaskCompletionSource();

        builder.Eventing.Subscribe<DummyEvent>(async (@event, ct) =>
        {
            blockAssertionTcs.SetResult();
            Interlocked.Increment(ref hitCount);
            await blockFirstSubscriptionTcs.Task;
        });

        builder.Eventing.Subscribe<DummyEvent>((@event, ct) =>
        {
            Interlocked.Increment(ref hitCount);
            return Task.CompletedTask;
        });

        var pendingPublish = builder.Eventing.PublishAsync(new DummyEvent(), EventDispatchBehavior.BlockingSequential);

        await blockAssertionTcs.Task.DefaultTimeout();
        Assert.Equal(1, hitCount);
        blockFirstSubscriptionTcs.SetResult();
        await pendingPublish.DefaultTimeout();
        Assert.Equal(2, hitCount);
    }

    [Fact]
    public async Task EventsCanBePublishedBlockConcurrent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var hitCount = 0;
        var blockAssertionSub1 = new TaskCompletionSource();
        var blockAssertionSub2 = new TaskCompletionSource();
        var blockSubscriptionCompletion = new TaskCompletionSource();

        builder.Eventing.Subscribe<DummyEvent>(async (@event, ct) =>
        {
            Interlocked.Increment(ref hitCount);
            blockAssertionSub1.SetResult();
            await blockSubscriptionCompletion.Task;
        });

        builder.Eventing.Subscribe<DummyEvent>(async (@event, ct) =>
        {
            Interlocked.Increment(ref hitCount);
            blockAssertionSub2.SetResult();
            await blockSubscriptionCompletion.Task;
        });

        var pendingPublish = builder.Eventing.PublishAsync(new DummyEvent(), EventDispatchBehavior.BlockingConcurrent);

        await Task.WhenAll(blockAssertionSub1.Task, blockAssertionSub2.Task).DefaultTimeout();
        Assert.Equal(2, hitCount);
        blockSubscriptionCompletion.SetResult();
        await pendingPublish.DefaultTimeout();
    }

    [Fact]
    public async Task EventsCanBePublishedNonBlockingConcurrent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var hitCount = 0;
        var blockAssertionSub1 = new TaskCompletionSource();
        var blockAssertionSub2 = new TaskCompletionSource();
        var blockSubscriptionExecution = new TaskCompletionSource();

        builder.Eventing.Subscribe<DummyEvent>(async (@event, ct) =>
        {
            await blockSubscriptionExecution.Task;
            Interlocked.Increment(ref hitCount);
            blockAssertionSub1.SetResult();
        });

        builder.Eventing.Subscribe<DummyEvent>(async (@event, ct) =>
        {
            await blockSubscriptionExecution.Task;
            Interlocked.Increment(ref hitCount);
            blockAssertionSub2.SetResult();
        });

        await builder.Eventing.PublishAsync(new DummyEvent(), EventDispatchBehavior.NonBlockingConcurrent).DefaultTimeout();

        blockSubscriptionExecution.SetResult();
        await Task.WhenAll(blockAssertionSub1.Task, blockAssertionSub2.Task).DefaultTimeout();
        Assert.Equal(2, hitCount);
    }

    [Fact]
    public async Task EventsCanBePublishedNonBlockingSequential()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var hitCount = 0;
        var blockEventSub1 = new TaskCompletionSource();
        var blockEventSub2 = new TaskCompletionSource();
        var blockAssert1 = new TaskCompletionSource();
        var blockAssert2 = new TaskCompletionSource();
        var blockAssert3 = new TaskCompletionSource();

        builder.Eventing.Subscribe<DummyEvent>(async (@event, ct) =>
        {
            blockAssert1.SetResult();
            await blockEventSub1.Task;
            Interlocked.Increment(ref hitCount);
            blockAssert2.SetResult();
            await blockEventSub2.Task;
        });

        builder.Eventing.Subscribe<DummyEvent>((@event, ct) =>
        {
            Interlocked.Increment(ref hitCount);
            blockAssert3.SetResult();
            return Task.CompletedTask;
        });

        await builder.Eventing.PublishAsync(new DummyEvent(), EventDispatchBehavior.NonBlockingSequential).DefaultTimeout();

        // Make sure that we are zero when we enter
        // the first handler.
        await blockAssert1.Task.DefaultTimeout();
        Assert.Equal(0, hitCount);

        // Give the second handler a chance to run,
        // it shouldn't and hit count should
        // still be zero.
        await Task.Delay(1000);
        Assert.Equal(0, hitCount);

        // After we unblock the first sub
        // we update the hit count and verify
        // that it has moved to 1.
        blockEventSub1.SetResult();
        await blockAssert2.Task.DefaultTimeout();
        Assert.Equal(1, hitCount);
        blockEventSub2.SetResult();

        // Now block until the second handler has
        // run and make sure it has incremented.
        await blockAssert3.Task.DefaultTimeout();
        Assert.Equal(2, hitCount);
    }

    [Fact]
    public void CanResolveIDistributedApplicationEventingFromDI()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        using var app = builder.Build();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        Assert.Equal(builder.Eventing, eventing);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ResourceEventsForContainersFireForSpecificResources()
    {
        var beforeResourceStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var redis = builder.AddRedis("redis")
            .OnBeforeResourceStarted((_, e, _) =>
            {
                Assert.NotNull(e.Services);
                Assert.NotNull(e.Resource);
                beforeResourceStartedTcs.TrySetResult();
                return Task.CompletedTask;
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        await beforeResourceStartedTcs.Task.DefaultTimeout();

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ResourceEventsForContainersFireForAllResources()
    {
        var countdownEvent = new CountdownEvent(2);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddRedis("redis1");
        builder.AddRedis("redis2");

        // Should be called twice ... once for each event.
        builder.Eventing.Subscribe<BeforeResourceStartedEvent>((e, ct) =>
        {
            Assert.NotNull(e.Services);
            Assert.NotNull(e.Resource);
            countdownEvent.Signal();
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        var fired = countdownEvent.Wait(TimeSpan.FromSeconds(10));

        Assert.True(fired);
        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    public async Task LifeycleHookAnalogousEventsFire()
    {
        var beforeStartEventFired = new ManualResetEventSlim();
        var afterEndpointsAllocatedEventFired = new ManualResetEventSlim();
        var afterResourcesCreatedEventFired = new ManualResetEventSlim();

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Eventing.Subscribe<BeforeStartEvent>((e, ct) =>
        {
            Assert.NotNull(e.Services);
            Assert.NotNull(e.Model);
            beforeStartEventFired.Set();
            return Task.CompletedTask;
        });
#pragma warning disable CS0618 // Type or member is obsolete
        builder.Eventing.Subscribe<AfterEndpointsAllocatedEvent>((e, ct) =>
        {
            Assert.NotNull(e.Services);
            Assert.NotNull(e.Model);
            afterEndpointsAllocatedEventFired.Set();
            return Task.CompletedTask;
        });
#pragma warning restore CS0618 // Type or member is obsolete
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((e, ct) =>
        {
            Assert.NotNull(e.Services);
            Assert.NotNull(e.Model);
            afterResourcesCreatedEventFired.Set();
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        await app.StartAsync();

        var allFired = ManualResetEvent.WaitAll(
            [beforeStartEventFired.WaitHandle, afterEndpointsAllocatedEventFired.WaitHandle, afterResourcesCreatedEventFired.WaitHandle],
            TimeSpan.FromSeconds(10)
            );

        Assert.True(allFired);
        await app.StopAsync();
    }

    [Fact]
    public async Task ResourceStoppedEventCanBeSubscribedTo()
    {
        var eventFired = false;
        var resourceStopped = default(IResource);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddResource(new TestResource("test-resource"))
            .OnResourceStopped((res, evt, ct) =>
            {
                eventFired = true;
                resourceStopped = res;
                Assert.NotNull(evt.Services);
                Assert.Equal(res, evt.Resource);
                Assert.NotNull(evt.ResourceEvent);
                Assert.Equal(res, evt.ResourceEvent.Resource);
                return Task.CompletedTask;
            });

        // Verify the subscription was registered (the event handler is stored in the eventing service)
        Assert.NotNull(resource);

        // This test focuses on verifying subscription registration and callback structure.
        // The following integration test handles actual event firing with complex setup.
        using var app = builder.Build();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Manually fire the event to test the subscription
        var testSnapshot = new CustomResourceSnapshot
        {
            ResourceType = "TestResource",
            Properties = []
        };
        var testResourceEvent = new ResourceEvent(resource.Resource, "test-resource", testSnapshot);
        var testEvent = new ResourceStoppedEvent(resource.Resource, app.Services, testResourceEvent);
        await eventing.PublishAsync(testEvent, CancellationToken.None);

        Assert.True(eventFired);
        Assert.Equal(resource.Resource, resourceStopped);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ResourceStoppedEventFiresWhenResourceStops()
    {
        var resourceStoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var redis = builder.AddRedis("redis")
            .OnResourceStopped((resource, e, _) =>
            {
                Assert.NotNull(e.Services);
                Assert.NotNull(e.Resource);
                Assert.Equal(resource, e.Resource);
                resourceStoppedTcs.TrySetResult();
                return Task.CompletedTask;
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        // Get the resource notification service to wait for the resource to start
        await app.ResourceNotifications.WaitForResourceAsync("redis", KnownResourceStates.Running).DefaultTimeout();

        await app.ResourceCommands.ExecuteCommandAsync("redis", KnownResourceCommands.StopCommand);

        // Verify that ResourceStoppedEvent was fired
        await resourceStoppedTcs.Task.DefaultTimeout();
    }

    public class DummyEvent : IDistributedApplicationEvent
    {
    }

    private sealed class TestResource(string name) : IResource
    {
        public string Name { get; } = name;
        public ResourceAnnotationCollection Annotations { get; } = new();
    }
}
