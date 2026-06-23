// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Cli;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

public class TestLoopCoordinatorTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task NoOpsWhenNotInTestMode()
    {
        // Without ASPIRE_CLI_TEST_MODE the coordinator must not touch the lifetime, even though
        // a test resource is present. This is what keeps `aspire run` behaving normally.
        var configuration = new ConfigurationBuilder().Build();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var stopCalled = false;
        var lifetime = new HostLifetimeStub(() => stopCalled = true);

        var coordinator = CreateCoordinator(configuration, model, notificationService, lifetime);

        await coordinator.StartAsync(CancellationToken.None).DefaultTimeout();
        await coordinator.StopAsync(CancellationToken.None).DefaultTimeout();

        Assert.False(stopCalled);
    }

    [Fact]
    public async Task StopsImmediatelyWhenNoTestResources()
    {
        // In test mode with nothing marked as a test resource there is nothing to bound the run,
        // so the coordinator should shut the app host down rather than hang like an interactive run.
        var configuration = CreateTestModeConfiguration();
        var model = new DistributedApplicationModel(new[] { new FakeResource("api") });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var coordinator = CreateCoordinator(configuration, model, notificationService, lifetime);

        await coordinator.StartAsync(CancellationToken.None).DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task StopsWhenSingleTestResourceReachesTerminalState()
    {
        var configuration = CreateTestModeConfiguration();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var coordinator = CreateCoordinator(configuration, model, notificationService, lifetime);

        await coordinator.StartAsync(CancellationToken.None).DefaultTimeout();

        // The resource starts running and then finishes - only the terminal state should end the run.
        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.Running }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.Finished }).DefaultTimeout();

        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task WaitsForAllTestResourcesBeforeStopping()
    {
        var configuration = CreateTestModeConfiguration();
        var first = new FakeResource("tests-a");
        first.Annotations.Add(new TestAnnotation());
        var second = new FakeResource("tests-b");
        second.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { first, second });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var coordinator = CreateCoordinator(configuration, model, notificationService, lifetime);

        await coordinator.StartAsync(CancellationToken.None).DefaultTimeout();

        await notificationService.PublishUpdateAsync(first, snapshot => snapshot with { State = KnownResourceStates.Finished }).DefaultTimeout();

        // Only one of the two test resources is terminal, so the run must still be alive.
        Assert.False(stopSignalTcs.Task.IsCompleted);

        await notificationService.PublishUpdateAsync(second, snapshot => snapshot with { State = KnownResourceStates.Exited }).DefaultTimeout();

        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task StopsWhenTestResourceFailsToStart()
    {
        // FailedToStart is terminal too, so a test that cannot even launch ends the run instead of
        // hanging it waiting for a state that will never arrive.
        var configuration = CreateTestModeConfiguration();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var coordinator = CreateCoordinator(configuration, model, notificationService, lifetime);

        await coordinator.StartAsync(CancellationToken.None).DefaultTimeout();

        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.FailedToStart }).DefaultTimeout();

        await stopSignalTcs.Task.DefaultTimeout();
    }

    private static IConfiguration CreateTestModeConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { KnownConfigNames.CliTestMode, "true" } })
            .Build();
    }

    private TestLoopCoordinator CreateCoordinator(
        IConfiguration configuration,
        DistributedApplicationModel model,
        ResourceNotificationService notificationService,
        IHostApplicationLifetime lifetime)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(testOutputHelper);
        });

        return new TestLoopCoordinator(
            configuration,
            model,
            notificationService,
            lifetime,
            loggerFactory.CreateLogger<TestLoopCoordinator>());
    }

    private sealed class FakeResource(string name) : IResource
    {
        public string Name { get; } = name;

        public ResourceAnnotationCollection Annotations { get; } = [];
    }
}

file sealed class HostLifetimeStub(Action stopImplementation) : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => throw new NotImplementedException();

    public CancellationToken ApplicationStopped => throw new NotImplementedException();

    public CancellationToken ApplicationStopping => throw new NotImplementedException();

    public void StopApplication() => stopImplementation();
}
