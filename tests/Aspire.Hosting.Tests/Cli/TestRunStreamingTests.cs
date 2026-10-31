// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class TestRunStreamingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task StreamIncludesOnlyAnnotatedResourcesAndAllReplicaOutcomesBeforeCompletion()
    {
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var tests = new ContainerResource("tests");
        tests.Annotations.Add(new TestAnnotation());
        tests.Annotations.Add(new ReplicaAnnotation(2));
        var api = new ContainerResource("api");
        var coordinator = new TestRunCoordinator(new DistributedApplicationModel([tests, api]), notifications,
            TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
        await notifications.PublishUpdateAsync(tests, s => s with { State = KnownResourceStates.Waiting });
        var events = new ConcurrentQueue<TestRunEvent>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = CollectAsync(coordinator.WatchAsync(TestContext.Current.CancellationToken), events, item =>
        {
            if (item.Resource is { ResourceId: "tests-1", State: "Running" })
            {
                observed.TrySetResult();
            }
        });

        await notifications.PublishUpdateAsync(api, s => s with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(tests, "tests-1", s => s with { State = KnownResourceStates.Running });
        await observed.Task.DefaultTimeout();
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(tests, "tests-1", s => s with { State = KnownResourceStates.Exited, ExitCode = 1 });
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(tests, "tests-2", s => s with { State = KnownResourceStates.Exited, ExitCode = 0 });
        await completion.DefaultTimeout();

        var results = events.ToArray();
        Assert.False(results[^1].Passed);
        Assert.All(results.Where(e => e.Resource is not null), e => Assert.Equal("tests", e.Resource!.ResourceName));
        var placeholders = results.Where(e => e.Resource?.State == KnownResourceStates.Waiting).ToArray();
        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, e => Assert.Null(e.Resource!.ResourceId));
        Assert.Collection(results.Where(e => e.Resource?.Outcome is "passed" or "failed").OrderBy(e => e.Resource!.ResourceId),
            first => Assert.Equal(("tests-1", "failed", 1), (first.Resource!.ResourceId, first.Resource.Outcome, first.Resource.ExitCode)),
            second => Assert.Equal(("tests-2", "passed", 0), (second.Resource!.ResourceId, second.Resource.Outcome, second.Resource.ExitCode)));
    }

    [Fact]
    public async Task AlreadyCompletedResourceReplaysItsFinalOutcomeBeforeAggregateCompletion()
    {
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var tests = new ContainerResource("tests");
        tests.Annotations.Add(new TestAnnotation());
        await notifications.PublishUpdateAsync(tests, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        var coordinator = new TestRunCoordinator(new DistributedApplicationModel([tests]), notifications,
            TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
        var events = new ConcurrentQueue<TestRunEvent>();
        await CollectAsync(coordinator.WatchAsync(TestContext.Current.CancellationToken), events).DefaultTimeout();

        var items = events.ToArray();
        Assert.True(items[^1].Passed);
        Assert.Contains(items[..^1], e => e.Resource is { ResourceId: "tests", Outcome: "passed", ExitCode: 0 });
    }

    [Fact]
    public async Task DisposingAnIncompleteStreamDrainsItsObservers()
    {
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var tests = new ContainerResource("tests");
        tests.Annotations.Add(new TestAnnotation());
        var coordinator = new TestRunCoordinator(new DistributedApplicationModel([tests]), notifications,
            TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
        await using var enumerator = coordinator.WatchAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().DefaultTimeout());

        await enumerator.DisposeAsync().AsTask().DefaultTimeout();
    }

    [Fact]
    public async Task UnknownExitCodeIsAnExplicitFailedOutcomeInTheStream()
    {
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var tests = new ContainerResource("tests");
        tests.Annotations.Add(new TestAnnotation());
        await notifications.PublishUpdateAsync(tests, s => s with { State = KnownResourceStates.Exited });
        var time = new FakeTimeProvider();
        var coordinator = new TestRunCoordinator(new DistributedApplicationModel([tests]), notifications,
            time, NullLogger<TestRunCoordinator>.Instance);
        var events = new ConcurrentQueue<TestRunEvent>();
        var completion = CollectAsync(coordinator.WatchAsync(TestContext.Current.CancellationToken), events);
        time.Advance(TestRunCoordinator.s_exitCodeTimeout);
        await completion.DefaultTimeout();

        Assert.Contains(events, e => e.Resource is { Outcome: "failed", ExitCode: null });
        Assert.False(events.Last().Passed);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false, false)]
    [InlineData(DistributedApplicationOperation.Run, true, true)]
    [InlineData(DistributedApplicationOperation.Publish, true, false)]
    public async Task ContextSetupIsRunOnlyAndRequiresTheCliSuppliedDestination(
        DistributedApplicationOperation operation, bool supplyPath, bool expectedCallback)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation, outputHelper);
        var path = Path.GetFullPath("test-output");
        if (supplyPath)
        {
            builder.Configuration[KnownConfigNames.TestOutputPath] = path;
        }
        TestRunContext? captured = null;
        var resource = builder.AddParameter("tests", "value");
        resource.WithTestRun(_ => throw new InvalidOperationException("Replaced callbacks must not execute."));
        resource.WithTestRun(context =>
        {
            captured = context;
            return Task.CompletedTask;
        });
        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model), cancellation.Token);

        Assert.Equal(expectedCallback, captured is not null);
        if (captured is not null)
        {
            Assert.Equal(path, captured.OutputPath);
            Assert.True(captured.CancellationToken.CanBeCanceled);
            cancellation.Cancel();
            Assert.True(captured.CancellationToken.IsCancellationRequested);
        }
        Assert.Single(resource.Resource.Annotations.OfType<TestAnnotation>());
    }

    private static async Task CollectAsync(IAsyncEnumerable<TestRunEvent> stream, ConcurrentQueue<TestRunEvent> events,
        Action<TestRunEvent>? observed = null)
    {
        await foreach (var item in stream)
        {
            events.Enqueue(item);
            observed?.Invoke(item);
        }
    }
}
