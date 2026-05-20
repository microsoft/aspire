// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRERADIUS001 // WithLegacyContainers is a transitional experimental API.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Tests for the opt-in legacy container fallback (<c>WithLegacyContainers()</c>).
///
/// When the env opts in, container workloads must emit as
/// <c>Applications.Core/containers@2023-10-01-preview</c> with the legacy
/// <c>properties.container.image</c> singular shape, parent to the legacy
/// application, and skip the UDT chain entirely when there are no UDT-bound
/// resource-type instances. This unblocks <c>rad deploy</c> on Radius installs
/// that have no recipe registered for <c>Radius.Compute/containers</c>.
/// </summary>
public class LegacyContainerFallbackTests
{
    private static string GenerateBicep(Action<IDistributedApplicationBuilder> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv").WithLegacyContainers();
        configure(builder);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        return context.GenerateBicep(model);
    }

    [Fact]
    public void ContainerOnly_EmitsLegacyContainerAndSkipsUdtChain()
    {
        var bicep = GenerateBicep(b => b.AddContainer("api", "nginx", "1.27"));

        // Legacy container emitted with singular `container.image`
        Assert.Contains("Applications.Core/containers@2023-10-01-preview", bicep);
        Assert.Contains("container: {", bicep);
        Assert.Contains("image: 'nginx:1.27'", bicep);

        // Legacy env + app are emitted (parent for the container).
        Assert.Contains("Applications.Core/environments@", bicep);
        Assert.Contains("Applications.Core/applications@", bicep);

        // No UDT chain since there are no UDT resources.
        Assert.DoesNotContain("Radius.Core/environments@", bicep);
        Assert.DoesNotContain("Radius.Core/applications@", bicep);
        Assert.DoesNotContain("Radius.Core/recipePacks@", bicep);
        Assert.DoesNotContain("Radius.Compute/containers", bicep);
    }

    [Fact]
    public void ContainerWithRedisConnection_RoutesContainerToLegacyAppAndConnectsToCache()
    {
        var bicep = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            b.AddContainer("api", "nginx", "1.27").WithReference(cache);
        });

        // Container is legacy; cache stays legacy too.
        Assert.Contains("Applications.Core/containers@", bicep);
        Assert.Contains("Applications.Datastores/redisCaches@", bicep);

        // Container parents to the legacy app and references the cache via connections.
        Assert.Contains("application: app.id", bicep);
        Assert.Contains("source: cache.id", bicep);

        // No UDT chain.
        Assert.DoesNotContain("Radius.Core/recipePacks@", bicep);
        Assert.DoesNotContain("Radius.Compute/containers", bicep);
    }

    [Fact]
    public void DefaultBehavior_StillEmitsUdtContainers()
    {
        // Sanity check: without WithLegacyContainers(), behaviour is unchanged.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "nginx", "1.27");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Radius.Compute/containers@2025-08-01-preview", bicep);
        Assert.DoesNotContain("Applications.Core/containers@", bicep);
    }
}
