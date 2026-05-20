// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class CustomRecipeTests
{
    [Fact]
    public void CustomRecipe_EmitsRecipeBlock_OnResourceTypeInstance()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache")
            .PublishAsRadiusResource(c =>
            {
                c.Recipe = new RadiusRecipe { Name = "my-custom-redis" };
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("recipe: {", bicep);
        Assert.Contains("name: 'my-custom-redis'", bicep);
    }

    [Fact]
    public void CustomRecipeWithParameters_EmitsParametersBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache")
            .PublishAsRadiusResource(c =>
            {
                c.Recipe = new RadiusRecipe
                {
                    Name = "premium-redis",
                    Parameters = { ["sku"] = "Premium" }
                };
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("name: 'premium-redis'", bicep);
        Assert.Contains("parameters: {", bicep);
        Assert.Contains("sku: 'Premium'", bicep);
    }

    [Fact]
    public void CustomRecipeWithRecipeLocation_RegistersInRecipePack()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache")
            .PublishAsRadiusResource(c =>
            {
                c.Recipe = new RadiusRecipe
                {
                    Name = "custom",
                    RecipeLocation = "ghcr.io/myorg/recipes/redis:1.0"
                };
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("ghcr.io/myorg/recipes/redis:1.0", bicep);
    }

    [Fact]
    public void NoCustomRecipe_OmitsRecipeProperty()
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

        // The Redis resource type instance should NOT have a recipe property
        // (recipe is only emitted when custom recipe is set)
        // Find the cache resource block and verify no recipe: within it
        var cacheBlockStart = bicep.IndexOf("name: 'cache'", StringComparison.Ordinal);
        Assert.True(cacheBlockStart > 0);
        var cacheBlockEnd = bicep.IndexOf("}", cacheBlockStart);
        var cacheBlock = bicep[cacheBlockStart..cacheBlockEnd];
        Assert.DoesNotContain("recipe:", cacheBlock);
    }
}
