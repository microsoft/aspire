// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class TestLoopCoordinatorEndToEndTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task AppHostShutsDownWhenTestResourceFinishes()
    {
        // This is the Phase 0 "walking demo" exercised in-process: boot a real app host in test mode
        // (as `aspire test` would via ASPIRE_CLI_TEST_MODE), mark a resource as a test resource, drive
        // it to a terminal state, and confirm the TestLoopCoordinator shuts the whole app host down on
        // its own without an interactive Ctrl+C.
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
        var pendingRun = app.RunAsync();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();

        // The "test" runs and then completes. Only the terminal state should end the run.
        await notificationService.PublishUpdateAsync(test.Resource, snapshot => snapshot with { State = KnownResourceStates.Running }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(test.Resource, snapshot => snapshot with { State = KnownResourceStates.Finished }).DefaultTimeout();

        // The app host should now tear itself down because every test resource has finished.
        await pendingRun.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    private sealed class DemoTestResource(string name) : Resource(name);
}
