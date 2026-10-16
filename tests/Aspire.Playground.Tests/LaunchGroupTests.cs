// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.TestUtilities;
using SamplesIntegrationTests;
using Xunit;

namespace Aspire.Playground.Tests;

[RequiresFeature(TestFeature.ContainerRuntime)]
public class LaunchGroupTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TestShopAdminResourcesUseTheirServiceLaunchGroups()
    {
        var builder = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.TestShop_AppHost), output);
        var pgAdmin = Assert.Single(builder.Resources, resource => resource.Name == "pgadmin");
        Assert.Equal("catalog", Assert.Single(pgAdmin.Annotations.OfType<LaunchGroupAnnotation>()).LaunchGroupName);
        foreach (var admin in builder.Resources.Where(resource => resource.Name is "rediscommander" or "redisinsight"))
        {
            Assert.Equal("basket", Assert.Single(admin.Annotations.OfType<LaunchGroupAnnotation>()).LaunchGroupName);
        }
        await using var app = await builder.BuildAsync();
    }

    [Fact]
    public async Task TestShopPartialRunSupportsAdditionalResourceStarts()
    {
        var builder = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.TestShop_AppHost), output);
        builder.Configuration["LaunchGroup"] = "catalog";
        await using var app = await builder.BuildAsync();

        using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            await app.StartAsync(startup.Token);
        }

        using (var readiness = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            await Task.WhenAll(new[] { "postgres", "catalogdbapp", "catalogservice" }
                .Select(name => app.ResourceNotifications.WaitForResourceHealthyAsync(name, readiness.Token)));
        }

        var unselected = new[] { "basketcache", "basketservice", "messaging", "frontend", "orderprocessor", "apigateway" }
            .Concat(builder.Resources.Where(resource => resource.Name is "rediscommander" or "redisinsight").Select(resource => resource.Name));
        foreach (var name in unselected)
        {
            Assert.True(app.ResourceNotifications.TryGetCurrentState(name, out var current));
            Assert.Equal(KnownResourceStates.NotStarted, current.Snapshot.State?.Text);
        }

        using (var expansion = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            var result = await app.ResourceCommands.ExecuteCommandAsync("basketservice", KnownResourceCommands.StartCommand, expansion.Token);
            Assert.True(result.Success, result.Message);
        }
        using (var readiness = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            await Task.WhenAll(new[] { "basketcache", "basketservice", "messaging" }
                .Select(name => app.ResourceNotifications.WaitForResourceHealthyAsync(name, readiness.Token)));
        }

        foreach (var name in new[] { "frontend", "orderprocessor", "apigateway" })
        {
            Assert.True(app.ResourceNotifications.TryGetCurrentState(name, out var current));
            Assert.Equal(KnownResourceStates.NotStarted, current.Snapshot.State?.Text);
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await app.StopAsync(shutdown.Token);
    }
}
