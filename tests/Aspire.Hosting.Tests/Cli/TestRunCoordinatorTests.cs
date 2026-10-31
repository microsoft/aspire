// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Cli;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIRETESTING001 // Tests the experimental testing API.

[Trait("Partition", "4")]
public class TestRunCoordinatorTests
{
    [Fact]
    public async Task NoAnnotationsFailsExplicitly()
    {
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var coordinator = CreateCoordinator(notifications, new ContainerResource("api"));

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken));

        Assert.Equal("No test resources were found. Mark a resource with WithTesting() before running aspire test.", exception.Message);
    }

    [Theory]
    [InlineData("Exited", 0, true)]
    [InlineData("Finished", 0, true)]
    [InlineData("Exited", 42, false)]
    [InlineData("Finished", 42, false)]
    [InlineData("FailedToStart", null, false)]
    [InlineData("RuntimeUnhealthy", null, false)]
    public async Task AlreadyCompletedResourceUsesStateAndExitCode(string state, int? exitCode, bool passed)
    {
        var resource = TestResource("tests");
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(resource, s => s with { State = state, ExitCode = exitCode });
        var coordinator = CreateCoordinator(notifications, resource);

        Assert.Equal(passed, await coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken).DefaultTimeout());
    }

    [Theory]
    [InlineData("Exited", 1)]
    [InlineData("FailedToStart", null)]
    public async Task FailureStillWaitsForEveryAnnotatedResource(string state, int? exitCode)
    {
        var first = TestResource("first");
        var second = TestResource("second");
        var service = new ContainerResource("service");
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var coordinator = CreateCoordinator(notifications, first, second, service);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);

        await notifications.PublishUpdateAsync(first, s => s with { State = state, ExitCode = exitCode });
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(second, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.False(await completion.DefaultTimeout());
    }

    [Fact]
    public async Task WaitsForAllReplicasWithoutCountingDuplicateNotifications()
    {
        var resource = TestResource("tests");
        resource.Annotations.Add(new ReplicaAnnotation(2));
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var coordinator = CreateCoordinator(notifications, resource);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);

        await notifications.PublishUpdateAsync(resource, "tests-1", s => s with { State = KnownResourceStates.Exited, ExitCode = 1 });
        await notifications.PublishUpdateAsync(resource, "tests-1", s => s with { State = KnownResourceStates.Exited, ExitCode = 1 });
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(resource, "tests-2", s => s with { State = KnownResourceStates.Exited, ExitCode = 0 });

        Assert.False(await completion.DefaultTimeout());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ExitedBeforeExitCodeWaitsForLaterNotification(int exitCode, bool passed)
    {
        var resource = TestResource("tests");
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Exited });
        var coordinator = CreateCoordinator(notifications, resource);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);

        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(resource, s => s with { ExitCode = exitCode });

        Assert.Equal(passed, await completion.DefaultTimeout());
    }

    [Theory]
    [InlineData("Exited")]
    [InlineData("Finished")]
    public async Task UnknownExitCodeTimesOutAsFailureAndStillWaitsForOtherTests(string state)
    {
        var unknown = TestResource("unknown");
        var remaining = TestResource("remaining");
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(unknown, s => s with { State = state });
        var timeProvider = new FakeTimeProvider();
        var coordinator = new TestRunCoordinator(new DistributedApplicationModel([unknown, remaining]), notifications,
            timeProvider, NullLogger<TestRunCoordinator>.Instance);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);

        timeProvider.Advance(TestRunCoordinator.s_exitCodeTimeout);
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(remaining, s => s with { State = KnownResourceStates.Exited, ExitCode = 0 });

        Assert.False(await completion.DefaultTimeout());
    }

    [Fact]
    public async Task CancellationDoesNotProduceAPassingOutcome()
    {
        var resource = TestResource("tests");
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var cts = new CancellationTokenSource();
        var coordinator = CreateCoordinator(notifications, resource);
        var completion = coordinator.WaitForCompletionAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.DefaultTimeout());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidReplicaCountDoesNotPassAnEmptyRun(int replicas)
    {
        var resource = TestResource("tests");
        resource.Annotations.Add(new ReplicaAnnotation(replicas));
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var coordinator = CreateCoordinator(notifications, resource);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Test resource 'tests' has no runnable replicas.", exception.Message);
    }

    [Fact]
    public async Task RapidCompletionDuringSubscriptionIsNotLost()
    {
        var first = TestResource("first");
        var second = TestResource("second");
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        var publishing = Task.WhenAll(
            notifications.PublishUpdateAsync(first, s => s with { State = KnownResourceStates.Exited, ExitCode = 0 }),
            notifications.PublishUpdateAsync(second, s => s with { State = KnownResourceStates.Exited, ExitCode = 0 }));
        var coordinator = CreateCoordinator(notifications, first, second);

        Assert.True(await coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken).DefaultTimeout());
        await publishing;
    }

    [Fact]
    public void WithTestingSupportsAnyResourceAndIsChainable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = new ContainerResource("custom");
        var resourceBuilder = builder.AddResource(resource);

        Assert.Same(resourceBuilder, resourceBuilder.WithTesting());
        Assert.IsType<TestingAnnotation>(Assert.Single(resource.Annotations));
    }

    private static ContainerResource TestResource(string name)
    {
        var resource = new ContainerResource(name);
        resource.Annotations.Add(new TestingAnnotation());

        return resource;
    }

    private static TestRunCoordinator CreateCoordinator(ResourceNotificationService notifications, params IResource[] resources) =>
        new(new DistributedApplicationModel(resources), notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
}
