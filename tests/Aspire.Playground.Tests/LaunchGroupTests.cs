// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests TestShop launch-group scenarios.

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
    public async Task TestShopFrontendDependenciesHaveExplicitMembership()
    {
        var builder = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.TestShop_AppHost), output);
        foreach (var name in new[]
        {
            "postgres", "catalogdb", "catalogdbapp", "catalogservice", "basketcache",
            "basketservice", "messaging", "frontend", "apigateway",
            "storefront-promotion", "low-stock-threshold", "checkout-signing-key"
        })
        {
            var resource = Assert.Single(builder.Resources, resource => resource.Name == name);
            Assert.Contains(resource.Annotations.OfType<LaunchGroupAnnotation>(),
                annotation => annotation.LaunchGroupName == "frontend");
        }
        await using var app = await builder.BuildAsync();
    }
}
