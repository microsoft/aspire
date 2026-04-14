// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Expressions;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class ConfigureRadiusInfrastructureTests
{
    [Fact]
    public void ConfigureCallback_CanMutateEnvironmentNamespace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                // Mutate the environment resource name in the AST
                var envResource = opts.Environments.OfType<RadiusEnvironmentConstruct>().First();
                envResource.EnvironmentName = "custom-env-name";
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("name: 'custom-env-name'", bicep);
    }

    [Fact]
    public void ConfigureCallback_CanAddCustomResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                var custom = new RadiusResourceTypeConstruct(
                    "customres", "Custom.Type/things", "2025-01-01");
                custom.ResourceName = "my-custom-thing";
                opts.ResourceTypeInstances.Add(custom);
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Custom.Type/things@2025-01-01", bicep);
        Assert.Contains("name: 'my-custom-thing'", bicep);
    }

    [Fact]
    public void ConfigureCallback_CanRemoveResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                // Remove all resource type instances
                opts.ResourceTypeInstances.Clear();
            });
        builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var options = context.BuildOptions(model);

        // Resource type instances should be empty after clearing
        Assert.Empty(options.ResourceTypeInstances);
    }

    [Fact]
    public void ConfigureCallback_RunsAfterPublishAsRadiusResource_LastWriteWins()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                // Override: change the cache resource name in the AST
                if (opts.ResourceTypeInstances.OfType<RadiusResourceTypeConstruct>()
                    .FirstOrDefault(r => r.ResourceName.Value == "cache") is { } cacheResource)
                {
                    cacheResource.ResourceName = "overridden-cache";
                }
            });
        builder.AddRedis("cache")
            .PublishAsRadiusResource(c =>
            {
                c.Recipe = new Radius.Models.RadiusRecipe { Name = "original-recipe" };
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // ConfigureRadiusInfrastructure should override: name changed
        Assert.Contains("name: 'overridden-cache'", bicep);
        // But the recipe from PublishAsRadiusResource should still be present
        // (since we only changed the name, not the recipe)
        Assert.Contains("original-recipe", bicep);
    }

    [Fact]
    public void NullConfigure_ThrowsArgumentNullException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv");

        Assert.Throws<ArgumentNullException>(() =>
            env.ConfigureRadiusInfrastructure(null!));
    }

    [Fact]
    public void ConfigureCallback_TypedCollectionAccess_NoOfTypeNeeded()
    {
        // L5: Typed access means callbacks can reach RadiusEnvironmentConstruct
        // directly from options.Environments without OfType<>().
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        RadiusEnvironmentConstruct? capturedEnv = null;
        RadiusApplicationConstruct? capturedApp = null;
        RadiusRecipePackConstruct? capturedRecipePack = null;
        RadiusResourceTypeConstruct? capturedResource = null;
        RadiusContainerConstruct? capturedContainer = null;

        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                capturedEnv = opts.Environments[0];
                capturedApp = opts.Applications[0];
                capturedRecipePack = opts.RecipePacks[0];
                capturedResource = opts.ResourceTypeInstances[0];
                capturedContainer = opts.Containers[0];
            });
        builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        _ = context.GenerateBicep(model);

        Assert.NotNull(capturedEnv);
        Assert.NotNull(capturedApp);
        Assert.NotNull(capturedRecipePack);
        Assert.NotNull(capturedResource);
        Assert.NotNull(capturedContainer);
    }

    [Fact]
    public void ConfigureCallback_BicepIdentifierRename_PropagatesToAllReferences()
    {
        // L5: Renaming BicepIdentifier inside a callback should propagate to
        // every cross-reference (env.RecipePacks, app.EnvironmentId,
        // resource.ApplicationId/.EnvironmentId, container.ApplicationId,
        // container connection sources).
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Environments[0].BicepIdentifier = "renamed_env";
                opts.Applications[0].BicepIdentifier = "renamed_app";
                opts.RecipePacks[0].BicepIdentifier = "renamed_pack";
                var cache = opts.ResourceTypeInstances.First(r => r.ResourceName.Value == "cache");
                cache.BicepIdentifier = "renamed_cache";
            });
        builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(builder.CreateResourceBuilder(
                builder.Resources.OfType<RedisResource>().First()));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // New identifiers must appear as declarations
        Assert.Contains("resource renamed_env", bicep);
        Assert.Contains("resource renamed_app", bicep);
        Assert.Contains("resource renamed_pack", bicep);
        Assert.Contains("resource renamed_cache", bicep);

        // And all cross-references must use the new names, never the old ones.
        Assert.Contains("renamed_pack.id", bicep);
        Assert.Contains("renamed_env.id", bicep);
        Assert.Contains("renamed_app.id", bicep);
        Assert.Contains("renamed_cache.id", bicep);

        // The old auto-generated identifiers must not leak into references.
        // Use leading-space match so "app.id" doesn't false-match inside "renamed_app.id".
        Assert.DoesNotContain(" myenv.id", bicep);
        Assert.DoesNotContain(" recipepack.id", bicep);
        Assert.DoesNotContain(" app.id", bicep);
        Assert.DoesNotContain(" cache.id", bicep);
    }

    [Fact]
    public void ConfigureCallback_CanEditRecipeEntryViaRecipeLocation()
    {
        // L5: Callbacks can reach into recipe entries via typed access and edit
        // the renamed RecipeLocation property (L1).
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                var pack = opts.RecipePacks[0];
                foreach (var entry in pack.Recipes)
                {
                    entry.Value.Value!.RecipeLocation = "ghcr.io/myorg/recipes/override:v2";
                }
            });
        builder.AddPostgres("db");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("ghcr.io/myorg/recipes/override:v2", bicep);
        Assert.Contains("recipeLocation:", bicep);
    }

    [Fact]
    public void ConfigureCallback_CanOverrideApplicationEnvironmentIdWithoutRenaming()
    {
        // Regression test: previously RewireIdReferences unconditionally reset
        // app.EnvironmentId post-callback, silently clobbering direct edits.
        // Now the rewire only runs when the target's BicepIdentifier actually
        // changes, so this explicit assignment must survive.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                var app = opts.Applications[0];
                app.EnvironmentId = new IdentifierExpression("customEnvRef");
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var instance = builder.Build();
        var model = instance.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // The callback's explicit assignment must be preserved — the rewire
        // would have replaced it with `myenv.id`.
        Assert.Contains("customEnvRef", bicep);
        // We set `environment:` on the application, so `environment: myenv.id`
        // (the builder default) must not be present in the application block.
        var appIdx = bicep.IndexOf("resource app ", StringComparison.Ordinal);
        Assert.True(appIdx >= 0);
        var appBlockEnd = bicep.IndexOf("\n}", appIdx, StringComparison.Ordinal);
        var appBlock = bicep.Substring(appIdx, appBlockEnd - appIdx);
        Assert.DoesNotContain("environment: myenv.id", appBlock);
    }

    [Fact]
    public void ConfigureCallback_RewireRunsWhenParentIdentifierChanges()
    {
        // Complementary to the preservation test above: when a callback renames
        // a parent's BicepIdentifier, dependent `.id` references *do* get
        // rewired to the new identifier.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                var env = opts.Environments[0];
                env.BicepIdentifier = "renamedEnv";
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // App's environment reference must follow the rename.
        Assert.Contains("environment: renamedEnv.id", bicep);
        Assert.DoesNotContain("environment: myenv.id", bicep);
    }
}
