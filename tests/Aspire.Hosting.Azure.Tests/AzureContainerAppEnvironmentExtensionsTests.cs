// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureContainerAppEnvironmentExtensionsTests
{
    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureContainerAppEnvironmentResource()
    {
        // Arrange
        var containerAppEnvironmentResource = new AzureContainerAppEnvironmentResource("test-container-app-env", _ => { });
        var infrastructure = new AzureResourceInfrastructure(containerAppEnvironmentResource, "test-container-app-env");

        // Act - Call AddAsExistingResource twice
        var firstResult = containerAppEnvironmentResource.AddAsExistingResource(infrastructure);
        var secondResult = containerAppEnvironmentResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureContainerAppEnvironmentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-env-name");
        var existingResourceGroup = builder.AddParameter("existing-env-rg");

        var containerAppEnvironment = builder.AddAzureContainerAppEnvironment("test-container-app-env")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = containerAppEnvironment.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task WithAzureLogAnalyticsWorkspace_RespectsExistingWorkspaceInDifferentResourceGroup()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        
        // Create parameters for existing Log Analytics Workspace in resource group "X"
        var lawName = builder.AddParameter("log-env-shared-name");
        var lawResourceGroup = builder.AddParameter("log-env-shared-rg"); // resource group "X"
        
        // Create Log Analytics Workspace resource marked as existing in resource group "X"
        var logAnalyticsWorkspace = builder
            .AddAzureLogAnalyticsWorkspace("log-env-shared")
            .AsExisting(lawName, lawResourceGroup);

        // Create Container App Environment in resource group "Y" that references the existing LAW
        var containerAppEnvironment = builder
            .AddAzureContainerAppEnvironment("app-host")  // This will be deployed to default resource group "Y"
            .WithAzureLogAnalyticsWorkspace(logAnalyticsWorkspace);

        // Verify that the LAW has the ExistingAzureResourceAnnotation
        Assert.True(logAnalyticsWorkspace.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAnnotation));
        Assert.Equal(lawName.Resource, existingAnnotation.Name);
        Assert.Equal(lawResourceGroup.Resource, existingAnnotation.ResourceGroup);

        // Verify that the Container App Environment has the AzureLogAnalyticsWorkspaceReferenceAnnotation
        Assert.True(containerAppEnvironment.Resource.TryGetLastAnnotation<AzureLogAnalyticsWorkspaceReferenceAnnotation>(out var workspaceRef));
        Assert.Same(logAnalyticsWorkspace.Resource, workspaceRef.Workspace);

        // Act & Assert - Generate bicep and verify using snapshot testing
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(containerAppEnvironment.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public void ContainerRegistry_ReturnsDefaultContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var containerAppEnvironment = builder.AddAzureContainerAppEnvironment("env");

        // The environment should have a default container registry set up
        var registry = containerAppEnvironment.Resource.ContainerRegistry;
        Assert.NotNull(registry);
        Assert.IsType<AzureContainerRegistryResource>(registry);
    }

    [Fact]
    public void ContainerRegistry_PrefersExplicitContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("myacr");
        var containerAppEnvironment = builder.AddAzureContainerAppEnvironment("env")
            .WithAzureContainerRegistry(acr);

        // Should return the explicitly set registry
        var registry = containerAppEnvironment.Resource.ContainerRegistry;
        Assert.Same(acr.Resource, registry);
    }

    [Fact]
    public void ContainerRegistry_ReturnsNullWhenNoRegistryConfigured()
    {
        // Create an environment resource without the builder to avoid automatic registry setup
        var environment = new AzureContainerAppEnvironmentResource("env", _ => { });

        Assert.Null(environment.ContainerRegistry);
    }

    [Fact]
    public void ContainerRegistry_ThrowsWhenNonAzureRegistryConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dockerRegistry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var containerAppEnvironment = builder.AddAzureContainerAppEnvironment("env")
            .WithContainerRegistry(dockerRegistry);

        // Should throw because a non-Azure registry is configured
        var exception = Assert.Throws<InvalidOperationException>(() => containerAppEnvironment.Resource.ContainerRegistry);
        Assert.Contains("not an Azure Container Registry", exception.Message);
        Assert.Contains("env", exception.Message);
    }

    [Fact]
    public async Task WithDelegatedSubnet_ConfiguresVnetConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");

        var containerAppEnvironment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(subnet);

        var (_, envBicep) = await AzureManifestUtils.GetManifestWithBicep(containerAppEnvironment.Resource);
        var (_, vnetBicep) = await AzureManifestUtils.GetManifestWithBicep(vnet.Resource);

        await Verify(envBicep, extension: "bicep")
            .AppendContentAsFile(vnetBicep, "bicep", "vnet");
    }

    [Fact]
    public async Task AsExisting_PublishGeneratesThinModuleReferencingExistingEnvironment()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/12977.
        // When AsExisting is used on AddAzureContainerAppEnvironment, the published Bicep
        // must reference the existing managed environment instead of generating a new one
        // plus a new Log Analytics workspace and a new Aspire Dashboard component.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environmentName = builder.AddParameter("environmentName");
        var sharedResourceGroup = builder.AddParameter("sharedRg");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .AsExisting(environmentName, sharedResourceGroup);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(env.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task AsExisting_WithExplicitContainerRegistry_PublishGeneratesThinModule()
    {
        // When AsExisting is combined with an explicit AsExisting ACR (a common deployment
        // scenario where everything is pre-provisioned), the resulting Bicep should not
        // create either a new managed environment or a new container registry.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environmentName = builder.AddParameter("environmentName");
        var registryName = builder.AddParameter("registryName");
        var sharedResourceGroup = builder.AddParameter("sharedRg");

        var acr = builder.AddAzureContainerRegistry("acr")
            .AsExisting(registryName, sharedResourceGroup);

        var env = builder.AddAzureContainerAppEnvironment("env")
            .AsExisting(environmentName, sharedResourceGroup)
            .WithAzureContainerRegistry(acr);

        var model = new DistributedApplicationModel([env.Resource]);
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, env.Resource);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            r => r.Name == "env-roles-acr");
        Assert.Same(acr.Resource, roleAssignment.TargetAzureResource);
        Assert.Same(env.Resource, roleAssignment.OwnerResource);
        Assert.Same(sharedResourceGroup.Resource, roleAssignment.Scope?.ResourceGroup);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task WithAcrPullIdentity_PublishGeneratesEnvWithSuppliedIdentity()
    {
        // Greenfield environment, but the user has BYO'd a managed identity. The generated
        // Bicep should NOT declare an env_mi resource or an AcrPull role assignment - the
        // identity id should flow into the env module via a parameter and be emitted as
        // AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mi = builder.AddAzureUserAssignedIdentity("shared-mi");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .WithAcrPullIdentity(mi);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(env.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task WithAcrPullIdentity_AsExisting_PublishGeneratesThinModuleWithSuppliedIdentity()
    {
        // Full pre-provisioned scenario from https://github.com/microsoft/aspire/issues/12977:
        // existing env + existing ACR + BYO identity that already has AcrPull on the ACR.
        // The generated env module should reference existing resources only and contain
        // neither a new identity nor a new role assignment.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environmentName = builder.AddParameter("environmentName");
        var registryName = builder.AddParameter("registryName");
        var identityName = builder.AddParameter("identityName");
        var sharedResourceGroup = builder.AddParameter("sharedRg");

        var acr = builder.AddAzureContainerRegistry("acr")
            .AsExisting(registryName, sharedResourceGroup);

        var mi = builder.AddAzureUserAssignedIdentity("shared-mi")
            .AsExisting(identityName, sharedResourceGroup);

        var env = builder.AddAzureContainerAppEnvironment("env")
            .AsExisting(environmentName, sharedResourceGroup)
            .WithAzureContainerRegistry(acr)
            .WithAcrPullIdentity(mi);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(env.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task AsExisting_ThrowsWhenCombinedWithDelegatedSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environmentName = builder.AddParameter("environmentName");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("aca-subnet", "10.0.0.0/23");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .AsExisting(environmentName, (IResourceBuilder<ParameterResource>?)null)
            .WithDelegatedSubnet(subnet);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.GetManifestWithBicep(env.Resource));

        Assert.Contains("existing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WithDelegatedSubnet", ex.Message);
    }

    [Fact]
    public async Task AsExisting_ThrowsWhenContainerAppDeclaresVolumeMount()
    {
        // When the env is marked as existing, Aspire cannot provision the storage
        // account / file share that backs container app volume mounts (storage is
        // attached to the managed env, which we don't own here). Asserts the
        // configureInfrastructure callback throws a clear error in that case so the
        // guard isn't silently dropped by future refactors.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environmentName = builder.AddParameter("environmentName");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .AsExisting(environmentName, (IResourceBuilder<ParameterResource>?)null);

        builder.AddContainer("api", "myimage")
               .WithVolume("data", "/var/data");

        using var app = builder.Build();

        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.GetManifestWithBicep(env.Resource, skipPreparer: true));

        Assert.Equal(
            "The Azure Container App Environment 'env' is marked as existing but one or more " +
            "container apps targeted to it declare volume mounts. Volumes require provisioning storage on the " +
            "managed environment, which Aspire cannot do for an existing environment. Remove the volume mounts " +
            "or stop marking the environment as existing.",
            ex.Message);
    }

    [Fact]
    public async Task AsExisting_ThrowsWhenCombinedWithAzureLogAnalyticsWorkspace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environmentName = builder.AddParameter("environmentName");

        var logAnalyticsWorkspace = builder.AddAzureLogAnalyticsWorkspace("log");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .AsExisting(environmentName, (IResourceBuilder<ParameterResource>?)null)
            .WithAzureLogAnalyticsWorkspace(logAnalyticsWorkspace);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.GetManifestWithBicep(env.Resource));

        Assert.Equal(
            "The Azure Container App Environment 'env' is marked as existing but is also " +
            "configured with a Log Analytics workspace via WithAzureLogAnalyticsWorkspace. The existing managed " +
            "environment already owns its Log Analytics workspace and Aspire cannot reconfigure it. Remove the " +
            "WithAzureLogAnalyticsWorkspace call or stop marking the environment as existing.",
            ex.Message);
    }

    [Fact]
    public async Task WithAzureContainerRegistry_PublishSucceeds_WhenDefaultRegistryIsRedundant()
    {
        // Regression test for the publish-time error:
        //   "Step 'push-prereq' depends on unknown step 'login-to-acr-env-acr'"
        // which occurred when an explicit container registry was supplied to an
        // AzureContainerAppEnvironment. The env's prepare step removes the now-unused
        // default '{env}-acr' registry from the model during BeforeStart. Without
        // isolating the BeforeStart pipeline-resolve from the singleton pipeline,
        // that resolve would have appended the default registry's login step name
        // to the built-in 'push-prereq' step's DependsOnSteps list, leaving a stale
        // dependency edge that caused the publish-time ResolveStepsAsync to fail.
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var acr = builder.AddAzureContainerRegistry("acr");
        builder.AddAzureContainerAppEnvironment("env")
            .WithAzureContainerRegistry(acr);

        builder.AddContainer("api", "myimage");

        using var app = builder.Build();

        // Publishing will stop the app when it is done.
        await app.RunAsync();

        var mainBicepPath = Path.Combine(tempDir.Path, "main.bicep");
        Assert.True(File.Exists(mainBicepPath), $"Expected publish to produce '{mainBicepPath}'.");
        var mainBicep = await File.ReadAllTextAsync(mainBicepPath);

        var envBicepPath = Path.Combine(tempDir.Path, "env", "env.bicep");
        Assert.True(File.Exists(envBicepPath), $"Expected publish to produce '{envBicepPath}'.");
        var envBicep = await File.ReadAllTextAsync(envBicepPath);

        await Verify(mainBicep, "bicep")
            .AppendContentAsFile(envBicep, "bicep");
    }
}
