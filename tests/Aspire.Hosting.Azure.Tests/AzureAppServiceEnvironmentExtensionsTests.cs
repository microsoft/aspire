// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureAppServiceEnvironmentExtensionsTests
{
    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureAppServiceEnvironmentResource()
    {
        // Arrange
        var appServiceEnvironmentResource = new AzureAppServiceEnvironmentResource("test-app-service-env", _ => { });
        var infrastructure = new AzureResourceInfrastructure(appServiceEnvironmentResource, "test-app-service-env");

        // Act - Call AddAsExistingResource twice
        var firstResult = appServiceEnvironmentResource.AddAsExistingResource(infrastructure);
        var secondResult = appServiceEnvironmentResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureAppServiceEnvironmentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-appenv-name");
        var existingResourceGroup = builder.AddParameter("existing-appenv-rg");

        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("test-app-service-env")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = appServiceEnvironment.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void ContainerRegistry_ReturnsDefaultContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env");

        // The environment should have a default container registry set up
        var registry = appServiceEnvironment.Resource.ContainerRegistry;
        Assert.NotNull(registry);
        Assert.IsType<AzureContainerRegistryResource>(registry);
    }

    [Fact]
    public void ContainerRegistry_PrefersExplicitContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("myacr");
        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
            .WithAzureContainerRegistry(acr);

        // Should return the explicitly set registry
        var registry = appServiceEnvironment.Resource.ContainerRegistry;
        Assert.Same(acr.Resource, registry);
    }

    [Fact]
    public void ContainerRegistry_ReturnsNullWhenNoRegistryConfigured()
    {
        // Create an environment resource without the builder to avoid automatic registry setup
        var environment = new AzureAppServiceEnvironmentResource("env", _ => { });

        Assert.Null(environment.ContainerRegistry);
    }

    [Fact]
    public void ContainerRegistry_ThrowsWhenNonAzureRegistryConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dockerRegistry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
            .WithContainerRegistry(dockerRegistry);

        // Should throw because a non-Azure registry is configured
        var exception = Assert.Throws<InvalidOperationException>(() => appServiceEnvironment.Resource.ContainerRegistry);
        Assert.Contains("not an Azure Container Registry", exception.Message);
        Assert.Contains("env", exception.Message);
    }

    [Fact]
    public async Task CrossResourceGroupRegistry_UsesStandaloneAcrPullIdentityForFinalRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var firstRegistry = builder.AddAzureContainerRegistry("first-registry")
            .PublishAsExisting("firstacr", "first-resource-group");
        var provisionedRegistry = builder.AddAzureContainerRegistry("provisioned-registry");
        var finalRegistry = builder.AddAzureContainerRegistry("final-registry")
            .PublishAsExisting("myacr", "my-existing-resource-group");

        var env = builder.AddAzureAppServiceEnvironment("env")
            .WithAzureContainerRegistry(firstRegistry)
            .WithAzureContainerRegistry(provisionedRegistry)
            .WithAzureContainerRegistry(finalRegistry)
            .WithDashboard(false);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var identity = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>(), resource => resource.Name == "env-mi");
        var roles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), resource => resource.Name == "env-mi-roles-final-registry");
        Assert.Same(finalRegistry.Resource, roles.TargetAzureResource);

        var (_, envBicep) = await AzureManifestUtils.GetManifestWithBicep(env.Resource, skipPreparer: true);
        var (_, identityBicep) = await AzureManifestUtils.GetManifestWithBicep(identity, skipPreparer: true);
        var (rolesManifest, rolesBicep) = await AzureManifestUtils.GetManifestWithBicep(roles, skipPreparer: true);

        await Verify(envBicep, extension: "bicep")
            .AppendContentAsFile(identityBicep, "bicep", "identity")
            .AppendContentAsFile(rolesBicep, "bicep", "roles")
            .AppendContentAsFile(rolesManifest.ToString(), "json", "roles");
    }

    [Fact]
    public async Task SameScopeExistingRegistry_KeepsInlineAcrPullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddAzureContainerRegistry("registry")
            .PublishAsExisting("myacr", resourceGroup: null);

        builder.AddAzureAppServiceEnvironment("env")
            .WithAzureContainerRegistry(registry)
            .WithDashboard(false);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Null(model.Resources.OfType<AzureUserAssignedIdentityResource>().SingleOrDefault(resource => resource.Name == "env-mi"));
        Assert.Null(model.Resources.OfType<AzureRoleAssignmentResource>().SingleOrDefault(resource => resource.Name.StartsWith("env-mi-roles-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CrossResourceGroupRegistry_PreservesAcrPullIdentityAddedAfterEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddAzureContainerRegistry("registry")
            .PublishAsExisting("myacr", "my-existing-resource-group");
        var env = builder.AddAzureAppServiceEnvironment("env")
            .WithAzureContainerRegistry(registry)
            .WithDashboard(false);
        var identity = builder.AddAzureUserAssignedIdentity("env-mi");
        env.WithAcrPullIdentity(identity);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Same(identity.Resource, Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>(), resource => resource.Name == "env-mi"));
        Assert.Null(model.Resources.OfType<AzureRoleAssignmentResource>().SingleOrDefault(resource => resource.Name.StartsWith("env-mi-roles-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MultipleEnvironments_SharingCrossScopeRegistry_EachGetDistinctStandaloneAcrPullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // A single existing registry in another resource group, shared by two environments in the same app host.
        var registry = builder.AddAzureContainerRegistry("registry")
            .PublishAsExisting("myacr", "my-existing-resource-group");

        builder.AddAzureAppServiceEnvironment("env1")
            .WithAzureContainerRegistry(registry)
            .WithDashboard(false);
        builder.AddAzureAppServiceEnvironment("env2")
            .WithAzureContainerRegistry(registry)
            .WithDashboard(false);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Each environment promotes its own standalone identity, disambiguated by the environment's (model-unique) name.
        var identity1 = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>(), resource => resource.Name == "env1-mi");
        var identity2 = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>(), resource => resource.Name == "env2-mi");
        Assert.NotSame(identity1, identity2);

        // Each environment gets its own AcrPull role module scoped to the shared registry, so the two grants never collide.
        var roles1 = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), resource => resource.Name == "env1-mi-roles-registry");
        var roles2 = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), resource => resource.Name == "env2-mi-roles-registry");
        Assert.Same(registry.Resource, roles1.TargetAzureResource);
        Assert.Same(registry.Resource, roles2.TargetAzureResource);
    }
}
