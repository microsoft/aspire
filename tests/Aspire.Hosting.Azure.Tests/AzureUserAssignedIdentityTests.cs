#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Utils;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureUserAssignedIdentityTests
{
    [Fact]
    public async Task AddAzureUserAssignedIdentity_GeneratesExpectedResourcesAndBicep()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Act
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureUserAssignedIdentity_PublishAsExisting_Works()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .PublishAsExisting("existingidentity", "my-rg");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureUserAssignedIdentity_WithRoleAssignments_Works()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("cae");

        var registry = builder.AddAzureContainerRegistry("myregistry");
        builder.AddAzureUserAssignedIdentity("myidentity")
            .WithRoleAssignments(registry, [ContainerRegistryBuiltInRole.AcrPush]);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Collection(model.Resources,
            r => Assert.IsType<AzureEnvironmentResource>(r),
            r => Assert.IsType<AzureContainerRegistryResource>(r),
            r => Assert.IsType<AzureContainerAppEnvironmentResource>(r),
            r => Assert.IsType<AzureContainerRegistryResource>(r),
            r => Assert.IsType<AzureUserAssignedIdentityResource>(r),
            r =>
            {
                Assert.IsType<AzureRoleAssignmentResource>(r);
                Assert.Equal("myidentity-roles-myregistry", r.Name);
            });

        var identityResource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        var (_, identityBicep) = await GetManifestWithBicep(identityResource, skipPreparer: true);

        var registryResource = Assert.Single(model.Resources.OfType<AzureContainerRegistryResource>(), r => r.Name == "myregistry");
        var (_, registryBicep) = await GetManifestWithBicep(registryResource, skipPreparer: true);

        var identityRoleAssignments = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "myidentity-roles-myregistry");
        Assert.Same(registryResource, identityRoleAssignments.TargetAzureResource);
        Assert.Same(identityResource, identityRoleAssignments.OwnerResource);
        var (_, identityRoleAssignmentsBicep) = await GetManifestWithBicep(identityRoleAssignments, skipPreparer: true);

        await Verify(identityBicep, "bicep")
            .AppendContentAsFile(registryBicep, "bicep")
            .AppendContentAsFile(identityRoleAssignmentsBicep, "bicep");
    }

    [Fact]
    public async Task WithAzureUserAssignedIdentity_Works()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("cae");

        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        // Use generic AddProject with TestProject type
        var projectBuilder = builder.AddProject<TestProject>("myapp", launchProfileName: null);
        projectBuilder.WithAzureUserAssignedIdentity(identity);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Check that only one AzureUserAssignedIdentityResource is created, the one that we explicitly constructed
        var identityResource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Equal("myidentity", identityResource.Name);

        // Check for IComputeResource having the correct identity
        var computeResource = Assert.Single(model.Resources.OfType<IComputeResource>(), r => r.Name == "myapp");
        var identityAnnotation = Assert.Single(computeResource.Annotations.OfType<AppIdentityAnnotation>());
        Assert.Same(identity.Resource, identityAnnotation.IdentityResource);
        var deploymentTarget = Assert.Single(computeResource.Annotations.OfType<DeploymentTargetAnnotation>());

        var (_, computeResourceBicep) = await GetManifestWithBicep(deploymentTarget.DeploymentTarget, skipPreparer: true);

        await Verify(computeResourceBicep, extension: "bicep");
    }

    [Fact]
    public async Task WithAzureUserAssignedIdentity_WithRoleAssignments_Works()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("cae");

        // Use Azure Storage instead of Container Registry to test role assignments on WithReference
        var storage = builder.AddAzureStorage("mystorage");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        var projectBuilder = builder.AddProject<TestProject>("myapp", launchProfileName: null);
        projectBuilder
            .WithAzureUserAssignedIdentity(identity)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageAccountContributor);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Validate that only the resources we expect to see are in the model
        Assert.Collection(model.Resources,
            r => Assert.IsType<AzureEnvironmentResource>(r),
            r => Assert.IsType<AzureContainerRegistryResource>(r),
            r => Assert.IsType<AzureContainerAppEnvironmentResource>(r),
            r => Assert.IsType<AzureStorageResource>(r),
            r => Assert.IsType<AzureUserAssignedIdentityResource>(r),
            r => Assert.IsType<ProjectResource>(r),
            r => Assert.IsType<AzureRoleAssignmentResource>(r));

        // Verify the identity resource is the only one that exists
        var identityResource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Equal("myidentity", identityResource.Name);

        // Verify the compute resource has the identity annotation
        var computeResource = Assert.Single(model.Resources.OfType<IComputeResource>(), r => r.Name == "myapp");
        var identityAnnotation = Assert.Single(computeResource.Annotations.OfType<AppIdentityAnnotation>());
        Assert.Same(identity.Resource, identityAnnotation.IdentityResource);

        // Verify the role assignment resource for the project
        var roleAssignmentResource = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            r => r.Name == "myapp-roles-mystorage");
        Assert.Same(storage.Resource, roleAssignmentResource.TargetAzureResource);
        Assert.Same(computeResource, roleAssignmentResource.OwnerResource);

        // Get the Bicep for all resources
        var deploymentTarget = Assert.Single(computeResource.Annotations.OfType<DeploymentTargetAnnotation>());
        var (_, computeResourceBicep) = await GetManifestWithBicep(deploymentTarget.DeploymentTarget, skipPreparer: true);
        var (_, identityBicep) = await GetManifestWithBicep(identityResource, skipPreparer: true);
        var (_, roleBicep) = await GetManifestWithBicep(roleAssignmentResource, skipPreparer: true);

        await Verify(computeResourceBicep, extension: "bicep")
            .AppendContentAsFile(identityBicep, "bicep")
            .AppendContentAsFile(roleBicep, "bicep");
    }

    [Fact]
    public async Task WithAzureUserAssignedIdentity_WithRoleAssignments_AzureAppService_Works()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureAppServiceEnvironment("appservice");

        // Use Azure Storage instead of Container Registry
        var storage = builder.AddAzureStorage("mystorage");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        var projectBuilder = builder.AddProject<TestProject>("myapp", launchProfileName: null);
        projectBuilder
            .WithAzureUserAssignedIdentity(identity)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageAccountContributor);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Validate that only the resources we expect to see are in the model
        Assert.Collection(model.Resources,
            r => Assert.IsType<AzureEnvironmentResource>(r),
            r => Assert.IsType<AzureContainerRegistryResource>(r),
            r => Assert.IsType<AzureAppServiceEnvironmentResource>(r),
            r => Assert.IsType<AzureStorageResource>(r),
            r => Assert.IsType<AzureUserAssignedIdentityResource>(r),
            r => Assert.IsType<ProjectResource>(r),
            r => Assert.IsType<AzureRoleAssignmentResource>(r));

        // Verify the identity resource is the only one that exists
        var identityResource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Equal("myidentity", identityResource.Name);

        // Verify the compute resource has the identity annotation
        var computeResource = Assert.Single(model.Resources.OfType<IComputeResource>(), r => r.Name == "myapp");
        var identityAnnotation = Assert.Single(computeResource.Annotations.OfType<AppIdentityAnnotation>());
        Assert.Same(identity.Resource, identityAnnotation.IdentityResource);

        // Verify the role assignment resource for the project
        var roleAssignmentResource = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            r => r.Name == "myapp-roles-mystorage");
        Assert.Same(storage.Resource, roleAssignmentResource.TargetAzureResource);
        Assert.Same(computeResource, roleAssignmentResource.OwnerResource);

        // Get the Bicep for all resources
        var deploymentTarget = Assert.Single(computeResource.Annotations.OfType<DeploymentTargetAnnotation>());
        var (_, computeResourceBicep) = await GetManifestWithBicep(deploymentTarget.DeploymentTarget, skipPreparer: true);
        var (_, identityBicep) = await GetManifestWithBicep(identityResource, skipPreparer: true);
        var (_, roleBicep) = await GetManifestWithBicep(roleAssignmentResource, skipPreparer: true);

        await Verify(computeResourceBicep, extension: "bicep")
            .AppendContentAsFile(identityBicep, "bicep")
            .AppendContentAsFile(roleBicep, "bicep");
    }

    [Fact]
    public async Task WithAzureUserAssignedIdentity_NoEnvironment_ThrowsException()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storage = builder.AddAzureStorage("mystorage");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        var projectBuilder = builder.AddProject<TestProject>("myapp", launchProfileName: null);
        projectBuilder
            .WithAzureUserAssignedIdentity(identity);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ExecuteBeforeStartHooksAsync(app, default));
    }

    [Fact]
    public async Task WithAzureUserAssignedIdentity_WithRoleAssignments_MultipleProjects_Works()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("cae");

        // Use Azure Storage instead of Container Registry
        var storage = builder.AddAzureStorage("mystorage");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        var projectBuilder = builder.AddProject<TestProject>("myapp", launchProfileName: null);
        projectBuilder
            .WithAzureUserAssignedIdentity(identity)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageAccountContributor);
        var projectBuilder2 = builder.AddProject<TestProject>("myapp2", launchProfileName: null);
        projectBuilder2
            .WithAzureUserAssignedIdentity(identity)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataOwner);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Validate that only the resources we expect to see are in the model
        Assert.Collection(model.Resources,
            r => Assert.IsType<AzureEnvironmentResource>(r),
            r => Assert.IsType<AzureContainerRegistryResource>(r),
            r => Assert.IsType<AzureContainerAppEnvironmentResource>(r),
            r => Assert.IsType<AzureStorageResource>(r),
            r => Assert.IsType<AzureUserAssignedIdentityResource>(r),
            r => Assert.IsType<ProjectResource>(r),
            r => Assert.IsType<ProjectResource>(r),
            r => Assert.True(r is AzureRoleAssignmentResource { Name: "myapp-roles-mystorage" }),
            r => Assert.True(r is AzureRoleAssignmentResource { Name: "myapp2-roles-mystorage" }));

        // Verify the identity resource is the only one that exists
        var identityResource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Equal("myidentity", identityResource.Name);

        // Verify that both compute resources have the same identity annotation
        var computeResource = Assert.Single(model.Resources.OfType<IComputeResource>(), r => r.Name == "myapp");
        var identityAnnotation = Assert.Single(computeResource.Annotations.OfType<AppIdentityAnnotation>());
        var computeResource2 = Assert.Single(model.Resources.OfType<IComputeResource>(), r => r.Name == "myapp2");
        var identityAnnotation2 = Assert.Single(computeResource2.Annotations.OfType<AppIdentityAnnotation>());
        Assert.Same(identity.Resource, identityAnnotation.IdentityResource);
        Assert.Same(identity.Resource, identityAnnotation2.IdentityResource);

        // Verify the role assignment resource for the project
        var roleAssignmentResource = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            r => r.Name == "myapp-roles-mystorage");
        var roleAssignmentResource2 = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            r => r.Name == "myapp2-roles-mystorage");
        Assert.Same(storage.Resource, roleAssignmentResource.TargetAzureResource);
        Assert.Same(computeResource, roleAssignmentResource.OwnerResource);
        Assert.Same(storage.Resource, roleAssignmentResource2.TargetAzureResource);
        Assert.Same(computeResource2, roleAssignmentResource2.OwnerResource);
        // Each project uses the same identity, but assigns different roles
        Assert.NotSame(roleAssignmentResource, roleAssignmentResource2);

        // Get the Bicep for all resources
        var deploymentTarget = Assert.Single(computeResource.Annotations.OfType<DeploymentTargetAnnotation>());
        var deploymentTarget2 = Assert.Single(computeResource2.Annotations.OfType<DeploymentTargetAnnotation>());
        var (_, computeResourceBicep) = await GetManifestWithBicep(deploymentTarget.DeploymentTarget, skipPreparer: true);
        var (_, computeResourceBicep2) = await GetManifestWithBicep(deploymentTarget2.DeploymentTarget, skipPreparer: true);
        var (_, identityBicep) = await GetManifestWithBicep(identityResource, skipPreparer: true);
        var (_, roleBicep) = await GetManifestWithBicep(roleAssignmentResource, skipPreparer: true);
        var (_, roleBicep2) = await GetManifestWithBicep(roleAssignmentResource2, skipPreparer: true);

        await Verify(computeResourceBicep, extension: "bicep")
            .AppendContentAsFile(computeResourceBicep2, "bicep")
            .AppendContentAsFile(identityBicep, "bicep")
            .AppendContentAsFile(roleBicep, "bicep")
            .AppendContentAsFile(roleBicep2, "bicep");
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureUserAssignedIdentityResource()
    {
        // Arrange
        var userAssignedIdentityResource = new AzureUserAssignedIdentityResource("test-identity");
        var infrastructure = new AzureResourceInfrastructure(userAssignedIdentityResource, "test-identity");

        // Act - Call AddAsExistingResource twice
        var firstResult = userAssignedIdentityResource.AddAsExistingResource(infrastructure);
        var secondResult = userAssignedIdentityResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureUserAssignedIdentityResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-identity-name");
        var existingResourceGroup = builder.AddParameter("existing-identity-rg");

        var userAssignedIdentity = builder.AddAzureUserAssignedIdentity("test-identity")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = userAssignedIdentity.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "some-path";
    }
}
