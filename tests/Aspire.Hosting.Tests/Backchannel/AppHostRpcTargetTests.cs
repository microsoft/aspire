// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Resources;
using Aspire.Shared;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Backchannel;

[Trait("Partition", "4")]
public class AppHostRpcTargetTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchResourceWatchStatus_ReportsPolicyAndCurrentLogicalCoverage(bool defaultEnabled)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            [$"AppHost:Run:WatchEnabled={defaultEnabled}"]);
        var service = builder.AddExecutable("service", "unused", ".").WithInitialState(new CustomResourceSnapshot
        {
            ResourceType = "Project",
            Properties =
            [
                new(ResourceWatch.StateProperty, ResourceWatch.Pending),
                new(ResourceWatch.LogResourceProperty, "dotnet-watch-service")
            ]
        });
        builder.AddExecutable("ordinary", "unused", ".");
        using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();
        Assert.Contains(ResourceWatch.Capability, await target.GetCapabilitiesAsync(TestContext.Current.CancellationToken));
        await using var stream = target.WatchResourceWatchStatusAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(defaultEnabled, stream.Current.DefaultEnabled);
        var generation = stream.Current.GenerationId;
        Assert.NotEqual(Guid.Empty, generation);
        Assert.Empty(stream.Current.Resources);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(service.Resource, snapshot => snapshot);
        Assert.True(await stream.MoveNextAsync().AsTask().DefaultTimeout());
        var pending = Assert.Single(stream.Current.Resources);
        Assert.Equal("service", pending.Resource);
        Assert.Equal(ResourceWatch.Pending, pending.State);
        Assert.Equal("dotnet-watch-service", pending.LogResource);

        await notifications.PublishUpdateAsync(service.Resource, snapshot => snapshot with
        {
            Properties =
            [
                new(ResourceWatch.StateProperty, ResourceWatch.BuildFailed),
                new(ResourceWatch.ReasonProperty, "The build failed. Fix the code and save."),
                new(ResourceWatch.LogResourceProperty, "dotnet-watch-service")
            ]
        });
        Assert.True(await stream.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(generation, stream.Current.GenerationId);
        var failed = Assert.Single(stream.Current.Resources);
        Assert.Equal(ResourceWatch.BuildFailed, failed.State);
        Assert.Equal("The build failed. Fix the code and save.", failed.Reason);
        Assert.Equal("dotnet-watch-service", failed.LogResource);
    }

    [Fact]
    public async Task WatchResourceWatchStatus_EmptyModelsAndGenerationShutdownAreExplicit()
    {
        using var firstBuilder = TestDistributedApplicationBuilder.Create();
        using var firstApp = firstBuilder.Build();
        using var secondBuilder = TestDistributedApplicationBuilder.Create();
        using var secondApp = secondBuilder.Build();
        var first = firstApp.Services.GetRequiredService<AppHostRpcTarget>();
        var second = secondApp.Services.GetRequiredService<AppHostRpcTarget>();
        await using var firstStream = first.WatchResourceWatchStatusAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator();
        await using var secondStream = second.WatchResourceWatchStatusAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator();

        Assert.True(await firstStream.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.True(await secondStream.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Empty(firstStream.Current.Resources);
        Assert.NotEqual(firstStream.Current.GenerationId, secondStream.Current.GenerationId);
        first.CancelInflightRpcCalls();
        Assert.False(await firstStream.MoveNextAsync().AsTask().DefaultTimeout());
    }

    [Theory]
    [InlineData("--operation", "publish", false)]
    [InlineData("--operation", "publish", true)]
    [InlineData("--publisher", "manifest", false)]
    [InlineData("--publisher", "manifest", true)]
    [InlineData("--AppHost:Operation", "publish", false)]
    [InlineData("--AppHost:Operation", "publish", true)]
    public async Task GetDashboardUrlsAsync_InPublishMode_ThrowsWithPublishGuidance(string option, string value, bool disableDashboard)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = disableDashboard,
            outputHelper,
            option, value);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => target.GetDashboardUrlsAsync(TestContext.Current.CancellationToken).DefaultTimeout());

        Assert.Equal(MessageStrings.PublishModeNotSupportedByRunCommand, exception.Message);
    }

    [Fact]
    public async Task GetDashboardUrlsAsync_InRunModeWithDashboardDisabled_ReturnsUnhealthy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true,
            outputHelper);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var result = await target.GetDashboardUrlsAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(result.DashboardHealthy);
        Assert.Null(result.BaseUrlWithLoginToken);
        Assert.Null(result.CodespacesUrlWithLoginToken);
    }
}
