// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
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

    [Fact]
    public async Task ReloadWatchedResources_RebuildsOneInstancePerWatchGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var rebuilt = new ConcurrentQueue<string>();
        var api = AddRebuildableResource(builder, "api", rebuilt, context => CommandResults.Success($"Rebuilt '{context.ResourceName}'."));
        var worker = AddRebuildableResource(builder, "worker", rebuilt, context => CommandResults.Success($"Rebuilt '{context.ResourceName}'."));
        var web = AddRebuildableResource(builder, "web", rebuilt, _ => CommandResults.Failure("The fresh build failed."));
        var excluded = AddRebuildableResource(builder, "excluded", rebuilt, _ => CommandResults.Success());
        var ordinary = AddRebuildableResource(builder, "ordinary", rebuilt, _ => CommandResults.Success());
        using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await PublishWatchStateAsync(notifications, api.Resource, "api-b", ResourceWatch.Watching, "dotnet-watch");
        await PublishWatchStateAsync(notifications, api.Resource, "api-a", ResourceWatch.Watching, "dotnet-watch");
        await PublishWatchStateAsync(notifications, worker.Resource, "worker", ResourceWatch.BuildFailed, "dotnet-watch");
        await PublishWatchStateAsync(notifications, web.Resource, "web", ResourceWatch.Failed, "dotnet-watch-web");
        await PublishWatchStateAsync(notifications, excluded.Resource, "excluded", ResourceWatch.Excluded, "excluded");
        await notifications.PublishUpdateAsync(ordinary.Resource, snapshot => snapshot);
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();
        Assert.Contains(ResourceWatch.ReloadCapability, await target.GetCapabilitiesAsync(TestContext.Current.CancellationToken));

        var result = await target.ReloadWatchedResourcesAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Collection(result.Groups,
            group =>
            {
                Assert.Equal("dotnet-watch", group.LogResource);
                Assert.Equal(["api", "worker"], group.Resources);
                Assert.True(group.Success);
                Assert.Equal("Rebuilt 'api-a'.", group.Message);
                // The most severe state among the group's instances: a failed build shows only on the projects that failed.
                Assert.Equal(ResourceWatch.BuildFailed, group.State);
            },
            group =>
            {
                Assert.Equal("dotnet-watch-web", group.LogResource);
                Assert.Equal(["web"], group.Resources);
                Assert.False(group.Success);
                Assert.Equal("The fresh build failed.", group.Message);
                Assert.Equal(ResourceWatch.Failed, group.State);
            });
        Assert.Equal(["api-a", "web"], rebuilt.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReloadWatchedResources_ReportsTheWatchStateThatTheRebuildLeft()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExecutable("api", "unused", ".");
        api.WithAnnotation(new ResourceCommandAnnotation(
            KnownResourceCommands.RebuildCommand, "Rebuild", _ => ResourceCommandState.Enabled,
            async context =>
            {
                // Rebuild publishes the group's state before it returns.
                await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(api.Resource, "api", snapshot => snapshot with
                {
                    Properties = [.. snapshot.Properties.Where(property => property.Name != ResourceWatch.StateProperty),
                        new(ResourceWatch.StateProperty, ResourceWatch.BuildFailed)]
                });
                return CommandResults.Failure("The fresh build failed.");
            }, null, arguments: null, null, null, null, false));
        using var app = builder.Build();
        await PublishWatchStateAsync(app.Services.GetRequiredService<ResourceNotificationService>(),
            api.Resource, "api", ResourceWatch.Watching, "dotnet-watch");
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var result = await target.ReloadWatchedResourcesAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var group = Assert.Single(result.Groups);
        Assert.False(group.Success);
        Assert.Equal(ResourceWatch.BuildFailed, group.State);
    }

    [Fact]
    public async Task ReloadWatchedResources_PrefersARunningInstanceOfTheGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var rebuilt = new ConcurrentQueue<string>();
        var api = AddRebuildableResource(builder, "api", rebuilt, context => CommandResults.Success($"Rebuilt '{context.ResourceName}'."));
        var worker = AddRebuildableResource(builder, "worker", rebuilt, context => CommandResults.Success($"Rebuilt '{context.ResourceName}'."));
        var web = AddRebuildableResource(builder, "web", rebuilt, context => CommandResults.Success($"Rebuilt '{context.ResourceName}'."));
        using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await PublishWatchStateAsync(notifications, api.Resource, "api", ResourceWatch.Watching, "dotnet-watch");
        await notifications.PublishUpdateAsync(api.Resource, "api", snapshot => snapshot with { State = KnownResourceStates.Exited });
        await PublishWatchStateAsync(notifications, worker.Resource, "worker", ResourceWatch.Watching, "dotnet-watch");
        await notifications.PublishUpdateAsync(worker.Resource, "worker", snapshot => snapshot with { State = KnownResourceStates.Running });
        await PublishWatchStateAsync(notifications, web.Resource, "web-a", ResourceWatch.Watching, "dotnet-watch-web");
        await PublishWatchStateAsync(notifications, web.Resource, "web-b", ResourceWatch.Watching, "dotnet-watch-web");
        await notifications.PublishUpdateAsync(web.Resource, "web-b", snapshot => snapshot with { State = KnownResourceStates.Running });
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var result = await target.ReloadWatchedResourcesAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Collection(result.Groups,
            group =>
            {
                Assert.Equal("dotnet-watch", group.LogResource);
                Assert.Equal(["api", "worker"], group.Resources);
                Assert.Equal("Rebuilt 'worker'.", group.Message);
            },
            group =>
            {
                Assert.Equal("dotnet-watch-web", group.LogResource);
                Assert.Equal(["web"], group.Resources);
                Assert.Equal("Rebuilt 'web-b'.", group.Message);
            });
        Assert.Equal(["web-b", "worker"], rebuilt.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReloadWatchedResources_WithoutWatchGroupsDoesNothing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var rebuilt = new ConcurrentQueue<string>();
        var ordinary = AddRebuildableResource(builder, "ordinary", rebuilt, _ => CommandResults.Success());
        using var app = builder.Build();
        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(ordinary.Resource, snapshot => snapshot);
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var result = await target.ReloadWatchedResourcesAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Empty(result.Groups);
        Assert.Empty(rebuilt);
    }

    [Theory]
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

    private static IResourceBuilder<ExecutableResource> AddRebuildableResource(
        IDistributedApplicationBuilder builder, string name, ConcurrentQueue<string> rebuilt, Func<ExecuteCommandContext, ExecuteCommandResult> rebuild) =>
        builder.AddExecutable(name, "unused", ".").WithAnnotation(new ResourceCommandAnnotation(
            KnownResourceCommands.RebuildCommand, "Rebuild", _ => ResourceCommandState.Enabled,
            context =>
            {
                rebuilt.Enqueue(context.ResourceName);
                return Task.FromResult(rebuild(context));
            }, null, arguments: null, null, null, null, false));

    private static Task PublishWatchStateAsync(
        ResourceNotificationService notifications, IResource resource, string id, string state, string logResource) =>
        notifications.PublishUpdateAsync(resource, id, snapshot => snapshot with
        {
            Properties =
            [
                .. snapshot.Properties,
                new(ResourceWatch.StateProperty, state),
                new(ResourceWatch.LogResourceProperty, logResource)
            ]
        });
}
