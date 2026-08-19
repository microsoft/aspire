// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class BicepGenerationMultiResourceTests
{
    [Fact]
    public void MultipleResources_AllResourceTypesPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");
        builder.AddSqlServer("sqlserver");
        builder.AddMongoDB("mongo");
        builder.AddRabbitMQ("messaging", userName: builder.AddParameter("messaginguser"));
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Each resource type instance should be present. Redis and RabbitMQ moved onto their
        // Radius.* UDTs in Radius 0.60; MongoDB and SQL Server still emit the legacy portable type
        // because no Kubernetes recipe is published for their UDTs.
        Assert.Contains("Radius.Data/redisCaches@2025-08-01-preview", bicep);
        Assert.Contains("Radius.Messaging/rabbitMQ@2025-08-01-preview", bicep);
        Assert.Contains("Applications.Datastores/sqlDatabases@2023-10-01-preview", bicep);
        Assert.Contains("Applications.Datastores/mongoDatabases@2023-10-01-preview", bicep);
    }

    [Fact]
    public void MultipleResources_RecipePackContainsAllTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");
        builder.AddSqlServer("sqlserver");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Recipe pack should have entries for each resource type
        Assert.Contains("ghcr.io/radius-project/kube-recipes/rediscaches:latest", bicep);
        Assert.Contains("ghcr.io/radius-project/recipes/local-dev/sqldatabases:latest", bicep);
    }

    [Fact]
    public void MultipleResources_EachHasApplicationReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Resources should reference the application
        Assert.Contains("application: app.id", bicep);
    }

    [Fact]
    public void MultipleResources_EnvironmentReferencesRecipePack()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Environment should reference recipe pack
        Assert.Contains("recipepack.id", bicep);
    }

    [Fact]
    public void MultipleContainers_AllContainersPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("frontend", "myapp/frontend", "latest");
        builder.AddContainer("backend", "myapp/backend", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("name: 'frontend'", bicep);
        Assert.Contains("name: 'backend'", bicep);
    }
}
