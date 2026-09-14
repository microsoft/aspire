// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class BicepSyntaxValidationTests
{
    [Fact]
    public void GeneratedBicep_StartsWithExtensionDirective()
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

        Assert.StartsWith("extension radius", bicep);
    }

    [Fact]
    public void AllResources_HaveCorrectApiVersion()
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

        // Radius resource types use the preview API version, legacy types use theirs
        // At minimum, the core resources (environment, application, recipePack) use RadiusApiVersion
        Assert.Contains($"Radius.Core/environments@{RadiusResourceTypes.RadiusApiVersion}", bicep);
        Assert.Contains($"Radius.Core/applications@{RadiusResourceTypes.RadiusApiVersion}", bicep);
    }

    [Fact]
    public void AllResources_HaveNameProperty()
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

        // All resources should emit name properties in the Bicep output
        Assert.Contains("name: 'myenv'", bicep);
        Assert.Contains("name: 'cache'", bicep);
        Assert.Contains("name: 'api'", bicep);
    }

    [Fact]
    public void ResourceTypeInstances_HaveApplicationAndEnvironmentReferences()
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

        // Resource type instances should reference both application and environment
        Assert.Contains("application: app.id", bicep);
        Assert.Contains("environment: myenv.id", bicep);
    }

    [Fact]
    public void Containers_HaveContainerBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var options = context.BuildOptions(model);

        foreach (var container in options.Containers.OfType<RadiusContainerConstruct>())
        {
            // Verify the container has an image set
            Assert.NotNull(container.Image);
            // Verify it has an application ID reference
            Assert.NotNull(container.ApplicationId);
        }
    }

    [Fact]
    public void BicepConfig_IsValidJson()
    {
        var configContent = BicepPostProcessor.RenderBicepConfig();

        // Should be valid JSON
        var doc = System.Text.Json.JsonDocument.Parse(configContent);
        Assert.NotNull(doc);

        // Should contain Radius extension pinned to the version constant the integration emits
        // (kept in lockstep with the deployed Radius install — see RadiusBicepExtension.Version).
        Assert.Contains("radius", configContent);
        Assert.Contains(RadiusBicepExtension.Reference, configContent);
        Assert.DoesNotContain(":latest", configContent);
    }

    [Fact]
    public void BicepConfig_HasExperimentalFeatures()
    {
        var configContent = BicepPostProcessor.RenderBicepConfig();

        Assert.Contains("experimentalFeaturesEnabled", configContent);
        Assert.Contains("extensibility", configContent);
    }

    [Fact]
    public void GeneratedBicep_ContainsAllExpectedResourceTypes()
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

        // All expected resource types should be present in the output
        Assert.Contains("Radius.Core/environments", bicep);
        Assert.Contains("Radius.Core/applications", bicep);
        Assert.Contains("Radius.Core/recipePacks", bicep);
        Assert.Contains("Applications.Datastores/redisCaches", bicep);
        Assert.Contains("Radius.Compute/containers", bicep);
    }
}
