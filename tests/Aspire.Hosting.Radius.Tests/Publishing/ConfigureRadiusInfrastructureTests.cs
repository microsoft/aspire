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
    public void ConfigureCallback_CanOverrideGeneratedResource_LastWriteWins()
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
        builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // ConfigureRadiusInfrastructure should override: name changed
        Assert.Contains("name: 'overridden-cache'", bicep);
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

    [Fact]
    public void ConfigureCallback_RenamingContainerName_Throws()
    {
        // Radius permits a top-level `name:` and `properties.containers` map key to differ, but
        // Aspire service discovery targets the original resource name, so renaming a container's
        // name would make the emitted `services__*` values point at a Service that is never
        // produced. The publisher must fail fast instead of emitting an unreachable manifest.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].ContainerName = "renamed";
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("api", ex.Message);
        Assert.Contains("renamed", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_RenamingPortlessContainerName_DoesNotThrow()
    {
        // A portless container has no Service and no `services__*` value addresses it, so Radius
        // permits its top-level `name:` to differ from the map key. Renaming it must be allowed —
        // the name-equality guard only applies to containers with a service-discovery contract.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].ContainerName = "renamed";
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var bicep = context.GenerateBicep(model);
        Assert.Contains("renamed", bicep);
    }

    [Fact]
    public void ConfigureCallback_SettingNonLiteralContainerName_Throws()
    {
        // A callback that replaces the resource-name literal with a computed Bicep expression can't
        // be verified against the `properties.containers` map key, and service discovery still
        // targets the literal name, so the publisher must reject it rather than emit a Service under
        // a name consumers never address.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].ContainerName = new IdentifierExpression("computedName");
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("api", ex.Message);
        Assert.Contains("non-literal", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_ChangingContainerPort_Throws()
    {
        // Service discovery URLs (`services__*`) are emitted from the pre-callback ports, so a
        // callback that changes a container port would leave consumers pointing at a stale port.
        // The publisher must fail fast instead of emitting an inconsistent manifest.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].Ports["http"] = new ContainerPortConstruct
                {
                    ContainerPort = 9999,
                    Protocol = "TCP",
                };
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("http", ex.Message);
        Assert.Contains("5000", ex.Message);
        Assert.Contains("9999", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_RemovingContainerPort_Throws()
    {
        // Removing a port that service discovery already emitted breaks cross-container calls just
        // like changing it, so the publisher must reject that too.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].Ports.Clear();
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("http", ex.Message);
        Assert.Contains("removed", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_ChangingContainerPortProtocol_Throws()
    {
        // Service discovery emits the pre-callback protocol as well as the port, so a callback that
        // changes only the protocol (leaving the port intact) still diverges from what consumers
        // were told and must be rejected.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].Ports["http"] = new ContainerPortConstruct
                {
                    ContainerPort = 5000,
                    Protocol = "UDP",
                };
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("http", ex.Message);
        Assert.Contains("TCP", ex.Message);
        Assert.Contains("UDP", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_SettingNonLiteralContainerPort_Throws()
    {
        // Service discovery emits a fixed literal port. A callback that swaps in a computed Bicep
        // expression could evaluate to a different value at deploy time, so it cannot be reconciled
        // with the already-emitted `services__*` port and must be rejected.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].Ports["http"] = new ContainerPortConstruct
                {
                    ContainerPort = new IdentifierExpression("computedPort"),
                    Protocol = "TCP",
                };
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("http", ex.Message);
        Assert.Contains("non-literal", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_RemovingPortlessContainer_DoesNotThrow()
    {
        // A portless container has no Service and no `services__*` value can address it, so removing
        // it in a callback is harmless. The service-discovery invariant must not reject this valid
        // customization — only removal of a container that had ports (and thus a Service) is rejected.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers.Clear();
            });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var bicep = context.GenerateBicep(model);
        Assert.DoesNotContain("myapp/api", bicep);
    }

    [Fact]
    public void ConfigureCallback_RemovingContainer_Throws()
    {
        // Service discovery already emitted `services__*` variables addressing the container, so a
        // callback that drops the workload entirely would leave consumers pointing at a Service that
        // is never produced. The publisher must fail fast.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers.Clear();
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("api", ex.Message);
        Assert.Contains("removed or replaced", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_AddingPortToPortlessContainerWithLongName_Throws()
    {
        // A portless container has no Service, so its long name is harmless until a callback adds the
        // first port. The Service-name length check therefore has to run on the FINAL container set:
        // here the callback introduces a port, and the resulting `{name}-{name}` Service name
        // overflows the 63-character Kubernetes limit.
        var longName = new string('a', 40);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].Ports["http"] = new ContainerPortConstruct
                {
                    ContainerPort = 8080,
                    Protocol = "TCP",
                };
            });
        builder.AddContainer(longName, "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains($"{longName}-{longName}", ex.Message);
        Assert.Contains("63", ex.Message);
    }

    [Fact]
    public void PublishingContainer_WithNameTooLongForKubernetesService_Throws()
    {
        // The Radius recipe names the ClusterIP Service `{name}-{name}`, and a Kubernetes Service
        // name is an RFC 1123 DNS label limited to 63 characters. Aspire allows resource names up to
        // 64 characters, so a container that declares ports but has a >31-character name would emit a
        // Service the control plane rejects. Fail fast at publish time with an actionable message.
        var longName = new string('a', 40);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer(longName, "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains($"{longName}-{longName}", ex.Message);
        Assert.Contains("63", ex.Message);
    }

    [Fact]
    public void ConfigureCallback_AddingDuplicatePortToContainer_Throws()
    {
        // The pre-callback dedup in ResolvePorts only covers the baseline ports. A callback that adds
        // a second endpoint resolving to the same (containerPort, protocol) as a preserved one would
        // make the recipe emit duplicate Kubernetes Service ports, so the post-callback validation
        // must re-run the dedup on the FINAL literal ports and fail fast.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.Containers[0].Ports["http2"] = new ContainerPortConstruct
                {
                    ContainerPort = 5000,
                    Protocol = "TCP",
                };
            });
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("api", ex.Message);
        Assert.Contains("5000/TCP", ex.Message);
    }
}
