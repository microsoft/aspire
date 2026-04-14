// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Models;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class ManualProvisioningTests
{
    [Fact]
    public void ManualProvisioning_EmitsResourceProvisioningManual()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddPostgres("postgres")
            .PublishAsRadiusResource(c =>
            {
                c.Provisioning = ResourceProvisioning.Manual;
                c.ConnectionStringOverrides["host"] = "postgres.default.svc.cluster.local";
                c.ConnectionStringOverrides["port"] = "5432";
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("resourceProvisioning: 'manual'", bicep);
        Assert.Contains("host: 'postgres.default.svc.cluster.local'", bicep);
        Assert.Contains("port: 5432", bicep);
    }

    [Fact]
    public void ManualProvisioning_ExcludedFromRecipePack()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddPostgres("postgres")
            .PublishAsRadiusResource(c =>
            {
                c.Provisioning = ResourceProvisioning.Manual;
                c.ConnectionStringOverrides["host"] = "localhost";
                c.ConnectionStringOverrides["port"] = "5432";
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var options = context.BuildOptions(model);

        // Manually provisioned resources should NOT have recipe entries in the recipe pack
        var recipePack = options.RecipePacks.OfType<RadiusRecipePackConstruct>().First();
        Assert.DoesNotContain(recipePack.Recipes.Keys, k => k.Contains("postgreSql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManualProvisioningWithRecipe_ThrowsAtConfigurationTime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddPostgres("postgres")
                .PublishAsRadiusResource(c =>
                {
                    c.Recipe = new RadiusRecipe { Name = "custom" };
                    c.Provisioning = ResourceProvisioning.Manual;
                }));
    }

    [Fact]
    public void AutomaticProvisioning_DoesNotEmitManualProperties()
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

        Assert.DoesNotContain("resourceProvisioning", bicep);
    }

    [Fact]
    public void ManualProvisioning_WithoutHostPort_EmitsManualWithoutHostPort()
    {
        // When manual provisioning is set without host/port, the Bicep output
        // contains resourceProvisioning: 'manual' but omits host/port properties.
        // This is valid — Radius allows manual provisioning with connection info
        // provided through other means (e.g., environment variables, secrets).
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddPostgres("postgres")
            .PublishAsRadiusResource(c =>
            {
                c.Provisioning = ResourceProvisioning.Manual;
                // Deliberately NOT setting host/port
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("resourceProvisioning: 'manual'", bicep);
        Assert.DoesNotContain("host:", bicep);
        Assert.DoesNotContain("port:", bicep);
    }
}
