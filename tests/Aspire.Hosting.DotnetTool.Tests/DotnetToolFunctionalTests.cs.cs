// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.DotnetTool.Tests;

public class DotnetToolFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task VerifyDotnetToolResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var resource = builder
            .AddDotnetTool("tool", "dotnet-ef")
            .WithArgs("--help");

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);
        var terminalState = await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.TerminalStates, cts.Token);

        Assert.Equal(KnownResourceStates.Finished, terminalState);

        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var resourceState));
        Assert.Equal(resourceState.Snapshot.ExitCode, 0);
    }

    [Fact]
    public async Task VerifyNonExistentDotnetToolResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var resource = builder
            .AddDotnetTool("tool", "dotnet-ef")
            .WithArgs("--help")
            .WithToolSource("./fake-package-feed")
            .WithToolIgnoreExistingFeeds();

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);
        var terminalState = await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.TerminalStates, cts.Token);
        Assert.Equal(KnownResourceStates.Finished, terminalState);

        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var resourceState));
        Assert.NotEqual(resourceState.Snapshot.ExitCode, 0);
    }

}
