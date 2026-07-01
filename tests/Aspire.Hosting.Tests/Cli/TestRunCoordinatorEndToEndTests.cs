// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class TestRunCoordinatorEndToEndTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task StreamsResultsForTestResourcesAgainstRealAppHost()
    {
        // Exercise the coordinator against a real built app host (resolved from DI rather than hand
        // constructed) to confirm `aspire test` wires it up: AddSingleton<TestRunCoordinator>() registers
        // it, and consuming WatchTestRunAsync against the app's real ResourceNotificationService streams a
        // Running update followed by a terminal result when a test resource finishes. The CLI consumes
        // this same stream over the backchannel and, on completion, stops the app host.
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        builder.Configuration[KnownConfigNames.CliTestMode] = "true";

        var test = builder.AddResource(new DemoTestResource("demo-tests"))
            .WithInitialState(new()
            {
                ResourceType = "DemoTest",
                State = KnownResourceStates.Starting,
                Properties = []
            })
            .ExcludeFromManifest()
            .WithTestRun();

        using var app = builder.Build();

        // Note: we deliberately do not call app.RunAsync() here. The orchestrator (DCP) is not needed to
        // exercise the coordinator, and avoiding it keeps the test deterministic on machines without a
        // container runtime. The ResourceNotificationService is a singleton that delivers updates without
        // the host being started.
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        var coordinator = app.Services.GetRequiredService<TestRunCoordinator>();

        var collect = Task.Run(async () =>
        {
            var updates = new List<TestRunUpdate>();
            await foreach (var update in coordinator.WatchTestRunAsync(CancellationToken.None))
            {
                updates.Add(update);
            }

            return updates;
        });

        // The "test" runs and then completes. Only the terminal state produces a result.
        await notificationService.PublishUpdateAsync(test.Resource, snapshot => snapshot with { State = KnownResourceStates.Running }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(test.Resource, snapshot => snapshot with { State = KnownResourceStates.Finished }).DefaultTimeout();

        var updates = await collect.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        Assert.Collection(updates,
            update => Assert.Equal(KnownTestRunStatuses.Running, update.Status),
            update => Assert.Equal(KnownTestRunStatuses.Passed, update.Status));
    }

    private sealed class DemoTestResource(string name) : Resource(name);
}
