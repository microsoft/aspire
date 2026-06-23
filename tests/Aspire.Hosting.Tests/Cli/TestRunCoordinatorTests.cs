// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

public class TestRunCoordinatorTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task CompletesEmptyWhenNotInTestMode()
    {
        // Without ASPIRE_CLI_TEST_MODE the coordinator must not stream anything, even though a test
        // resource is present. This is what keeps `aspire run` behaving normally.
        var configuration = new ConfigurationBuilder().Build();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var coordinator = CreateCoordinator(configuration, model, notificationService);

        var updates = await CollectAsync(coordinator).DefaultTimeout();

        Assert.Empty(updates);
    }

    [Fact]
    public async Task CompletesEmptyWhenNoTestResources()
    {
        // In test mode with nothing marked as a test resource there is nothing to bound the run, so the
        // stream completes immediately and the CLI treats it as a (warning) no-op rather than hanging.
        var configuration = CreateTestModeConfiguration();
        var model = new DistributedApplicationModel(new[] { new FakeResource("api") });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var coordinator = CreateCoordinator(configuration, model, notificationService);

        var updates = await CollectAsync(coordinator).DefaultTimeout();

        Assert.Empty(updates);
    }

    [Fact]
    public async Task EmitsRunningThenPassedWhenResourceFinishes()
    {
        var configuration = CreateTestModeConfiguration();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var coordinator = CreateCoordinator(configuration, model, notificationService);

        var collect = CollectAsync(coordinator);

        // The resource starts running and then finishes - only the terminal state produces a result.
        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.Running }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.Finished }).DefaultTimeout();

        var updates = await collect.DefaultTimeout();

        Assert.Collection(updates,
            u =>
            {
                Assert.Equal("integration-tests", u.Resource);
                Assert.Equal(KnownTestRunStatuses.Running, u.Status);
            },
            u =>
            {
                Assert.Equal("integration-tests", u.Resource);
                Assert.Equal(KnownTestRunStatuses.Passed, u.Status);
            });
    }

    [Fact]
    public async Task EmitsRunningForAllResourcesBeforeResults()
    {
        var configuration = CreateTestModeConfiguration();
        var first = new FakeResource("tests-a");
        first.Annotations.Add(new TestAnnotation());
        var second = new FakeResource("tests-b");
        second.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { first, second });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var coordinator = CreateCoordinator(configuration, model, notificationService);

        var collect = CollectAsync(coordinator);

        await notificationService.PublishUpdateAsync(first, snapshot => snapshot with { State = KnownResourceStates.Finished }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(second, snapshot => snapshot with { State = KnownResourceStates.Exited, ExitCode = 0 }).DefaultTimeout();

        var updates = await collect.DefaultTimeout();

        // Both resources report Running up front (so the CLI learns the total), then both report results.
        Assert.Equal(4, updates.Count);
        Assert.Equal(KnownTestRunStatuses.Running, updates[0].Status);
        Assert.Equal(KnownTestRunStatuses.Running, updates[1].Status);
        Assert.Equal(new[] { "tests-a", "tests-b" }, updates.Take(2).Select(u => u.Resource).OrderBy(n => n).ToArray());

        var results = updates.Skip(2).ToDictionary(u => u.Resource, u => u.Status);
        Assert.Equal(KnownTestRunStatuses.Passed, results["tests-a"]);
        Assert.Equal(KnownTestRunStatuses.Passed, results["tests-b"]);
    }

    [Fact]
    public async Task EmitsFailedWhenResourceFailsToStart()
    {
        // FailedToStart is terminal too, so a test that cannot even launch produces a Failed result
        // instead of hanging the stream waiting for a state that will never arrive.
        var configuration = CreateTestModeConfiguration();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var coordinator = CreateCoordinator(configuration, model, notificationService);

        var collect = CollectAsync(coordinator);

        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.FailedToStart }).DefaultTimeout();

        var updates = await collect.DefaultTimeout();

        Assert.Equal(KnownTestRunStatuses.Failed, Assert.Single(updates, u => u.Status != KnownTestRunStatuses.Running).Status);
    }

    [Fact]
    public async Task EmitsFailedWhenResourceExitsNonZero()
    {
        var configuration = CreateTestModeConfiguration();
        var resource = new FakeResource("integration-tests");
        resource.Annotations.Add(new TestAnnotation());
        var model = new DistributedApplicationModel(new[] { resource });
        var notificationService = ResourceNotificationServiceTestHelpers.Create();

        var coordinator = CreateCoordinator(configuration, model, notificationService);

        var collect = CollectAsync(coordinator);

        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with { State = KnownResourceStates.Exited, ExitCode = 1 }).DefaultTimeout();

        var updates = await collect.DefaultTimeout();

        var result = Assert.Single(updates, u => u.Status != KnownTestRunStatuses.Running);
        Assert.Equal(KnownTestRunStatuses.Failed, result.Status);
    }

    private static async Task<List<TestRunUpdate>> CollectAsync(TestRunCoordinator coordinator)
    {
        var updates = new List<TestRunUpdate>();
        await foreach (var update in coordinator.WatchTestRunAsync(CancellationToken.None).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static IConfiguration CreateTestModeConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { KnownConfigNames.CliTestMode, "true" } })
            .Build();
    }

    private TestRunCoordinator CreateCoordinator(
        IConfiguration configuration,
        DistributedApplicationModel model,
        ResourceNotificationService notificationService)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(testOutputHelper);
        });

        return new TestRunCoordinator(
            configuration,
            model,
            notificationService,
            loggerFactory.CreateLogger<TestRunCoordinator>());
    }

    private sealed class FakeResource(string name) : IResource
    {
        public string Name { get; } = name;

        public ResourceAnnotationCollection Annotations { get; } = [];
    }
}
