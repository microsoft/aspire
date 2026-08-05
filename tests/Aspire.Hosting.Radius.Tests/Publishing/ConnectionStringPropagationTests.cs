// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class ConnectionStringPropagationTests
{
    [Fact]
    public void WithReference_CreatesConnectionBlock_OnContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var cache = builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(cache);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Container should have a connections block with cache reference
        Assert.Contains("connections: {", bicep);
        Assert.Contains("source: cache.id", bicep);
    }

    [Fact]
    public void MultipleReferences_AllPresentInConnectionsBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var cache = builder.AddRedis("cache");
        var sql = builder.AddSqlServer("sqlserver");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(cache)
            .WithReference(sql);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("source: cache.id", bicep);
        Assert.Contains("source: sqlserver.id", bicep);
    }

    [Fact]
    public void NoReferences_OmitsConnectionsBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Container without references should NOT have a connections block. This AppHost
        // has only the single container, so asserting the whole Bicep output contains no
        // "connections:" block is sufficient.
        Assert.DoesNotContain("connections:", bicep);
    }

    [Fact]
    public void ChildResource_ResolvesToParent_InConnections()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var sql = builder.AddSqlServer("sqlserver").AddDatabase("mydb");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(sql);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Child resource (mydb) should resolve to parent (sqlserver) in connections
        Assert.Contains("source: sqlserver.id", bicep);
    }

}
