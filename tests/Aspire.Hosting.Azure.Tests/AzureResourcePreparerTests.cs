// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.SignalR;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureResourcePreparerTests
{
    [Fact]
    public void AzureRoleAssignmentResourceThrowsWhenOwnerAndIdentityAreInconsistent()
    {
        var target = new TestProvisioningResource("target");
        var identity = new AzureUserAssignedIdentityResource("identity");
        var owner = new TestProvisioningResource("owner");

        Assert.Throws<ArgumentException>("identityResource", () =>
            new AzureRoleAssignmentResource("roles", target, owner, identityResource: null, _ => { }));

        Assert.Throws<ArgumentException>("ownerResource", () =>
            new AzureRoleAssignmentResource("roles", target, ownerResource: null, identity, _ => { }));
    }

    private sealed class TestProvisioningResource(string name) : AzureProvisioningResource(name, _ => { });

    [Theory]
    [InlineData(DistributedApplicationOperation.Publish)]
    [InlineData(DistributedApplicationOperation.Run)]
    public async Task ThrowsExceptionsIfRoleAssignmentUnsupported(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);

        var storage = builder.AddAzureStorage("storage");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataReader);

        var app = builder.Build();

        if (operation == DistributedApplicationOperation.Publish)
        {
            var ex = Assert.Throws<InvalidOperationException>(app.Start);
            Assert.Contains("role assignments", ex.Message);
        }
        else
        {
            await app.StartAsync();
            // no exception is thrown in Run mode
        }
    }

    [Theory]
    [InlineData(true, DistributedApplicationOperation.Run)]
    [InlineData(false, DistributedApplicationOperation.Run)]
    [InlineData(true, DistributedApplicationOperation.Publish)]
    [InlineData(false, DistributedApplicationOperation.Publish)]
    public async Task AppliesDefaultRoleAssignmentsInRunModeIfReferenced(bool addContainerAppsInfra, DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        if (addContainerAppsInfra)
        {
            builder.AddAzureContainerAppEnvironment("env");
        }

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(blobs);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(storage.Resource.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaultAssignments));

        if (!addContainerAppsInfra || operation == DistributedApplicationOperation.Run)
        {
            // when AzureContainerAppsInfrastructure is not added, we always apply the default role assignments to a new 'storage-roles' resource.
            // The same applies when in RunMode and we are provisioning Azure resources for F5 local development.
            var storageRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "storage-roles");
            Assert.Same(storage.Resource, storageRoles.TargetAzureResource);
            Assert.Null(storageRoles.OwnerResource);

            var storageRolesManifest = await GetManifestWithBicep(storageRoles, skipPreparer: true);
            await Verify(storageRolesManifest.BicepText, extension: "bicep");

        }
        else
        {
            // in PublishMode when AzureContainerAppsInfrastructure is added, the DefaultRoleAssignmentsAnnotation
            // is copied to referencing resources' RoleAssignmentAnnotation.

            Assert.True(api.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out var apiRoleAssignments));
            Assert.Equal(storage.Resource, apiRoleAssignments.Target);
            Assert.Equal(defaultAssignments.Roles, apiRoleAssignments.Roles);
        }
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task AppliesRoleAssignmentsInRunMode(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDelegator, StorageBuiltInRole.StorageBlobDataReader)
            .WithReference(blobs);

        var api2 = builder.AddProject<Project>("api2", launchProfileName: null)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor)
            .WithReference(blobs);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        if (operation == DistributedApplicationOperation.Run)
        {
            // in RunMode, we apply the role assignments to a new 'storage-roles' resource, so the provisioned resource
            // adds these role assignments for F5 local development.
            var storageRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "storage-roles");
            Assert.Same(storage.Resource, storageRoles.TargetAzureResource);
            Assert.Null(storageRoles.OwnerResource);

            var storageRolesManifest = await GetManifestWithBicep(storageRoles, skipPreparer: true);
            await Verify(storageRolesManifest.BicepText, extension: "bicep");

        }
        else
        {
            // in PublishMode, the role assignments are copied to the referencing resources' RoleAssignmentAnnotation.
            Assert.True(api.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out var apiRoleAssignments));
            Assert.Equal(storage.Resource, apiRoleAssignments.Target);
            Assert.Collection(apiRoleAssignments.Roles,
                role => Assert.Equal(StorageBuiltInRole.StorageBlobDelegator.ToString(), role.Id),
                role => Assert.Equal(StorageBuiltInRole.StorageBlobDataReader.ToString(), role.Id));

            Assert.True(api2.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out var api2RoleAssignments));
            Assert.Equal(storage.Resource, api2RoleAssignments.Target);
            Assert.Single(api2RoleAssignments.Roles,
                role => role.Id == StorageBuiltInRole.StorageBlobDataContributor.ToString());
        }
    }

    [Fact]
    public async Task DoesNotApplyRoleAssignmentsInRunModeForEmulators()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddBicepTemplateString("foo", "");

        var dbsrv = builder.AddAzureSqlServer("dbsrv").RunAsContainer();
        var db = dbsrv.AddDatabase("db");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        // in RunMode, we skip applying the role assignments to a new 'dbsrv-roles' resource, since the storage is running as emulator.
        Assert.DoesNotContain(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "dbsrv-roles");
    }

    [Fact]
    public async Task FindsAzureReferencesFromArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        // the project doesn't WithReference or WithRoleAssignments, so it should get the default role assignments
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithArgs(context =>
            {
                context.Args.Add("--azure-blobs");
                context.Args.Add(blobs.Resource.ConnectionStringExpression);
            });

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(storage.Resource.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaultAssignments));

        Assert.True(api.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out var apiRoleAssignments));
        Assert.Equal(storage.Resource, apiRoleAssignments.Target);
        Assert.Equal(defaultAssignments.Roles, apiRoleAssignments.Roles);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_CreatesRoleResourcesForAggregateOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var identity = builder.AddAzureUserAssignedIdentity("aggregate-identity");
        var storage = builder.AddAzureStorage("storage");
        var keyVault = builder.AddAzureKeyVault("keyvault");

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new AppIdentityAnnotation(identity.Resource));
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => keyVault.Resource,
            CreateKeyVaultRoleDefinitions(KeyVaultBuiltInRole.KeyVaultSecretsUser)));
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var storageRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-storage");
        Assert.Same(storage.Resource, storageRoles.TargetAzureResource);
        Assert.Same(aggregate, storageRoles.OwnerResource);
        Assert.Same(identity.Resource, storageRoles.IdentityResource);

        var keyVaultRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-keyvault");
        Assert.Same(keyVault.Resource, keyVaultRoles.TargetAzureResource);
        Assert.Same(aggregate, keyVaultRoles.OwnerResource);
        Assert.Same(identity.Resource, keyVaultRoles.IdentityResource);

        var prerequisites = Assert.Single(aggregate.Annotations.OfType<DeploymentPrerequisitesAnnotation>()).Resources;
        Assert.Contains(storageRoles, prerequisites);
        Assert.Contains(keyVaultRoles, prerequisites);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_CreatesRoleResourcesWithoutComputeEnvironmentWhenTargetedAssignmentsSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var identity = builder.AddAzureUserAssignedIdentity("aggregate-identity");
        var storage = builder.AddAzureStorage("storage");

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new AppIdentityAnnotation(identity.Resource));
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.DoesNotContain(model.Resources, resource => resource is IAzureComputeEnvironmentResource);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-storage");
        Assert.Same(storage.Resource, roleAssignment.TargetAzureResource);
        Assert.Same(aggregate, roleAssignment.OwnerResource);
        Assert.Same(identity.Resource, roleAssignment.IdentityResource);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_ThrowsForAggregateOwnerWhenTargetedAssignmentsUnsupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storage = builder.AddAzureStorage("storage");

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("role assignments", ex.Message);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_SkipsNullTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var identity = builder.AddAzureUserAssignedIdentity("aggregate-identity");

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new AppIdentityAnnotation(identity.Resource));
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => null,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.DoesNotContain(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.OwnerResource == aggregate);
        Assert.Empty(aggregate.Annotations.OfType<DeploymentPrerequisitesAnnotation>());
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_DoesNotCacheNullTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var identity = builder.AddAzureUserAssignedIdentity("aggregate-identity");
        var storage = builder.AddAzureStorage("storage");

        AzureProvisioningResource? target = null;
        var roleAssignmentAnnotation = new RoleAssignmentAnnotation(
            () => target,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader));

        Assert.Throws<InvalidOperationException>(() => roleAssignmentAnnotation.Target);
        target = storage.Resource;

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new AppIdentityAnnotation(identity.Resource));
        aggregate.Annotations.Add(roleAssignmentAnnotation);
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-storage");
        Assert.Same(storage.Resource, roleAssignment.TargetAzureResource);
        Assert.Same(aggregate, roleAssignment.OwnerResource);
        Assert.Same(identity.Resource, roleAssignment.IdentityResource);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_CachesFirstNonNullTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var identity = builder.AddAzureUserAssignedIdentity("aggregate-identity");
        var storage = builder.AddAzureStorage("storage");
        var keyVault = builder.AddAzureKeyVault("keyvault");

        AzureProvisioningResource? target = storage.Resource;
        var roleAssignmentAnnotation = new RoleAssignmentAnnotation(
            () => target,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader));

        Assert.Same(storage.Resource, roleAssignmentAnnotation.Target);
        target = keyVault.Resource;

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new AppIdentityAnnotation(identity.Resource));
        aggregate.Annotations.Add(roleAssignmentAnnotation);
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-storage");
        Assert.Same(storage.Resource, roleAssignment.TargetAzureResource);
        Assert.DoesNotContain(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-keyvault");
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_DeduplicatesRolesForAggregateOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var identity = builder.AddAzureUserAssignedIdentity("aggregate-identity");
        var storage = builder.AddAzureStorage("storage");

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new AppIdentityAnnotation(identity.Resource));
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-storage");
        Assert.Same(storage.Resource, roleAssignment.TargetAzureResource);
        Assert.Same(aggregate, roleAssignment.OwnerResource);

        var manifest = await GetManifestWithBicep(roleAssignment, skipPreparer: true);
        var roleAssignmentCount = System.Text.RegularExpressions.Regex.Matches(manifest.BicepText, "Microsoft.Authorization/roleAssignments@").Count;
        Assert.Equal(1, roleAssignmentCount);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_PublishMode_CreatesIdentityForAggregateOwnerWithoutAppIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var storage = builder.AddAzureStorage("storage");

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));
        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var generatedIdentity = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>(), r => r.Name == "aggregate-identity");
        var identityAnnotation = Assert.Single(aggregate.Annotations.OfType<AppIdentityAnnotation>());
        Assert.Same(generatedIdentity, identityAnnotation.IdentityResource);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "aggregate-roles-storage");
        Assert.Same(generatedIdentity, roleAssignment.IdentityResource);
        Assert.Same(aggregate, roleAssignment.OwnerResource);
    }

    [Fact]
    public async Task DeferredRoleAssignmentAnnotation_RunMode_DoesNotCreateGlobalRoleAssignmentForAggregateOwnedTargetWithoutDirectReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var storage = builder.AddAzureStorage("storage")
            .ClearDefaultRoleAssignments();

        var aggregate = new TestProvisioningResource("aggregate");
        aggregate.Annotations.Add(new RoleAssignmentAnnotation(
            () => storage.Resource,
            CreateStorageRoleDefinitions(StorageBuiltInRole.StorageBlobDataReader)));

        builder.AddResource(aggregate);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.DoesNotContain(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.TargetAzureResource == storage.Resource);
    }

    [Fact]
    public async Task PublishDeploymentTargetIncludesComputedPrerequisitesInReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureAppServiceEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var peSubnet = vnet.AddSubnet("pe-subnet", "10.0.1.0/24");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");
        var queues = storage.AddBlobs("queues");

        var blobPE = peSubnet.AddPrivateEndpoint(blobs);
        var queuesPE = peSubnet.AddPrivateEndpoint(queues);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(blobs)
            .WithReference(queues);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleAssignmentResource = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "api-roles-storage");
        var deploymentTarget = Assert.IsAssignableFrom<AzureBicepResource>(api.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget);

        Assert.Same(storage.Resource, roleAssignmentResource.TargetAzureResource);
        Assert.Same(api.Resource, roleAssignmentResource.OwnerResource);
        Assert.Contains(roleAssignmentResource, deploymentTarget.References);
        Assert.Contains(blobPE.Resource, deploymentTarget.References);
        Assert.Contains(queuesPE.Resource, deploymentTarget.References);
    }

    [Fact]
    public async Task PipelineStepAfterBeforeStartCanInspectRoleAssignmentsForTargetAzureResource()
    {
        const string inspectRoleAssignmentsStepName = "inspect-keyvault-role-assignments";

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: inspectRoleAssignmentsStepName);
        builder.AddAzureContainerAppEnvironment("env");

        var keyVault = builder.AddAzureKeyVault("keyvault");
        var storage = builder.AddAzureStorage("storage");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataReader);

        var worker = builder.AddProject<Project>("worker", launchProfileName: null)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser);

        List<AzureRoleAssignmentResource>? keyVaultRoleAssignments = null;
        builder.Pipeline.AddStep(
            inspectRoleAssignmentsStepName,
            context =>
            {
                keyVaultRoleAssignments =
                    [.. context.Model.Resources
                        .OfType<AzureRoleAssignmentResource>()
                        .Where(resource => resource.TargetAzureResource == keyVault.Resource)
                        .OrderBy(resource => resource.Name, StringComparer.Ordinal)];

                return Task.CompletedTask;
            },
            dependsOn: WellKnownPipelineSteps.BeforeStart);

        using var app = builder.Build();
        await ExecutePipelineAsync(app);

        Assert.NotNull(keyVaultRoleAssignments);
        Assert.Collection(keyVaultRoleAssignments,
            resource =>
            {
                Assert.Equal("api-roles-keyvault", resource.Name);
                Assert.Same(keyVault.Resource, resource.TargetAzureResource);
                Assert.Same(api.Resource, resource.OwnerResource);
            },
            resource =>
            {
                Assert.Equal("worker-roles-keyvault", resource.Name);
                Assert.Same(keyVault.Resource, resource.TargetAzureResource);
                Assert.Same(worker.Resource, resource.OwnerResource);
            });

        var storageRoleAssignment = Assert.Single(
            app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.TargetAzureResource == storage.Resource);
        Assert.Same(api.Resource, storageRoleAssignment.OwnerResource);
    }

    [Fact]
    public async Task RoleAssignmentsAreNotDuplicatedWhenBeforeStartRunsBeforePublishPipeline()
    {
        const string inspectRoleAssignmentsStepName = "inspect-signalr-role-assignments";

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: inspectRoleAssignmentsStepName);
        builder.AddAzureContainerAppEnvironment("env");

        var signalR = builder.AddAzureSignalR("signalr");

        var serviceA = builder.AddProject<Project>("service-a", launchProfileName: null)
            .WithReference(signalR)
            .WithRoleAssignments(signalR, SignalRBuiltInRole.SignalRContributor);

        var serviceB = builder.AddProject<Project>("service-b", launchProfileName: null)
            .WithReference(signalR)
            .WithRoleAssignments(signalR, SignalRBuiltInRole.SignalRContributor);

        List<AzureRoleAssignmentResource>? signalRRoleAssignments = null;
        builder.Pipeline.AddStep(
            inspectRoleAssignmentsStepName,
            context =>
            {
                signalRRoleAssignments =
                    [.. context.Model.Resources
                        .OfType<AzureRoleAssignmentResource>()
                        .Where(resource => resource.TargetAzureResource == signalR.Resource)
                        .OrderBy(resource => resource.Name, StringComparer.Ordinal)];

                return Task.CompletedTask;
            },
            dependsOn: WellKnownPipelineSteps.BeforeStart);

        using var app = builder.Build();
        await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(signalRRoleAssignments);
        Assert.Collection(signalRRoleAssignments,
            resource =>
            {
                Assert.Equal("service-a-roles-signalr", resource.Name);
                Assert.Same(signalR.Resource, resource.TargetAzureResource);
                Assert.Same(serviceA.Resource, resource.OwnerResource);
            },
            resource =>
            {
                Assert.Equal("service-b-roles-signalr", resource.Name);
                Assert.Same(signalR.Resource, resource.TargetAzureResource);
                Assert.Same(serviceB.Resource, resource.OwnerResource);
            });

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Collection(
            model.Resources
                .OfType<ProjectResource>()
                .OrderBy(resource => resource.Name, StringComparer.Ordinal),
            resource => Assert.Single(
                resource.Annotations.OfType<DeploymentPrerequisitesAnnotation>().SelectMany(annotation => annotation.Resources),
                prerequisite => prerequisite.Name == "service-a-roles-signalr"),
            resource => Assert.Single(
                resource.Annotations.OfType<DeploymentPrerequisitesAnnotation>().SelectMany(annotation => annotation.Resources),
                prerequisite => prerequisite.Name == "service-b-roles-signalr"));
    }

    [Fact]
    public async Task GlobalRoleAssignmentsAreNotDuplicatedWhenBeforeStartRunsTwice()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(blobs);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);
        await ExecuteBeforeStartHooksAsync(app, default);

        var storageRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), resource => resource.Name == "storage-roles");
        Assert.Same(storage.Resource, storageRoles.TargetAzureResource);
        Assert.Null(storageRoles.OwnerResource);
        Assert.Single(storage.Resource.Annotations.OfType<RoleAssignmentResourceAnnotation>());
        Assert.Single(storageRoles.Annotations.OfType<ResourceRelationshipAnnotation>());
    }

    [Fact]
    public async Task NullEnvironmentVariableIsIgnored()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");

        // Create a project with an environment variable callback that sets a null value
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithEnvironment(context =>
            {
                // This simulates the issue where a callback adds a null value
                context.EnvironmentVariables["NULL_ENV"] = null!;
                context.EnvironmentVariables["VALID_ENV"] = "valid_value";
            });

        using var app = builder.Build();

        // This should not throw a NullReferenceException
        await ExecuteBeforeStartHooksAsync(app, default);

        // Test passes if we reach this point without exceptions
        Assert.True(true);
    }

    [Fact]
    public async Task NullCommandLineArgIsIgnored()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");

        // Create a project with a command line args callback that adds a null value
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithArgs(context =>
            {
                // This simulates the issue where a callback adds a null value
                context.Args.Add("--valid-arg");
                context.Args.Add(null!);
                context.Args.Add("another-valid-arg");
            });

        using var app = builder.Build();

        // This should not throw a NullReferenceException
        await ExecuteBeforeStartHooksAsync(app, default);

        // Test passes if we reach this point without exceptions
        Assert.True(true);
    }

    [Fact]
    public async Task CommandLineArgsCallbackContextHasCorrectExecutionContextDuringPublish()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        DistributedApplicationExecutionContext? capturedExecutionContext = null;

        // Create a project with a WithArgs callback that captures the ExecutionContext
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithArgs(context =>
            {
                // Capture the ExecutionContext to verify it's set correctly
                capturedExecutionContext = context.ExecutionContext;
            });

        using var app = builder.Build();

        // This should not throw - the ExecutionContext should be set correctly
        await ExecuteBeforeStartHooksAsync(app, default);

        // Verify the ExecutionContext was captured and is in Publish mode
        Assert.NotNull(capturedExecutionContext);
        Assert.True(capturedExecutionContext.IsPublishMode);
        Assert.False(capturedExecutionContext.IsRunMode);
    }

    /// <summary>
    /// Ensures that role assignments are only applied to direct references and not transitive ones.
    /// </summary>
    [Fact]
    public async Task AppliesRoleAssignmentsOnlyToDirectReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithReference(blobs);

        var api2 = builder.AddProject<Project>("api2", launchProfileName: null)
            .WithReference(api);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Collection(model.Resources.Select(r => r.Name),
            n => Assert.StartsWith("azure", n),
            n => Assert.Equal("env-acr", n),
            n => Assert.Equal("env-acr-pull-identity", n),
            n => Assert.Equal("env", n),
            n => Assert.Equal("storage", n),
            n => Assert.Equal("blobs", n),
            n => Assert.Equal("api", n),
            n => Assert.Equal("api2", n),
            n => Assert.Equal("api-identity", n),
            n => Assert.Equal("api-roles-storage", n),
            n => Assert.Equal("env-roles-env-acr", n));
    }

    [Fact]
    public async Task ViteAppDoesNotGetManagedIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithReference(blobs)
            .WaitFor(blobs);

        var frontend = builder.AddViteApp("frontend", "./frontend")
            .WithReference(api)
            .WithReference(blobs)
            .WaitFor(blobs);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Collection(model.Resources.Select(r => r.Name),
            n => Assert.StartsWith("azure", n),
            n => Assert.Equal("env-acr", n),
            n => Assert.Equal("env-acr-pull-identity", n),
            n => Assert.Equal("env", n),
            n => Assert.Equal("storage", n),
            n => Assert.Equal("blobs", n),
            n => Assert.Equal("api", n),
            n => Assert.Equal("frontend", n),
            n => Assert.Equal("api-identity", n),
            n => Assert.Equal("api-roles-storage", n),
            n => Assert.Equal("env-roles-env-acr", n));

        // The ViteApp should NOT get a managed identity since it is a BuildOnlyContainer resource,
        // even though it references the storage account. Only the API should get a managed identity.
        Assert.DoesNotContain(model.Resources, r => r.Name == "frontend-identity");
    }

    [Fact]
    public async Task ReferenceRoleAssignmentAnnotation_PublishMode_GrantsRolesOnImpliedTargetToConsumer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");

        // A non-Azure compute resource (the "agent" node app) that fronts the storage account: any
        // resource referencing it should be granted a role on storage even though storage is only a
        // transitive dependency that the IAzureResource-only reference walk cannot reach.
        var agent = builder.AddContainer("agent", "img:latest")
            .WithHttpEndpoint();
        agent.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(
            storage.Resource,
            new HashSet<RoleDefinition> { new(StorageBuiltInRole.StorageBlobDataReader.ToString(), nameof(StorageBuiltInRole.StorageBlobDataReader)) }));

        var consumer = builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(agent.GetEndpoint("http"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(consumer.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out var consumerRoleAssignments));
        Assert.Equal(storage.Resource, consumerRoleAssignments.Target);
        Assert.Single(consumerRoleAssignments.Roles, role => role.Id == StorageBuiltInRole.StorageBlobDataReader.ToString());
    }

    [Fact]
    public async Task ReferenceRoleAssignmentAnnotation_RunMode_AppliesRolesToGlobalRolesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");

        var agent = builder.AddContainer("agent", "img:latest")
            .WithHttpEndpoint();
        agent.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(
            storage.Resource,
            new HashSet<RoleDefinition> { new(StorageBuiltInRole.StorageBlobDataReader.ToString(), nameof(StorageBuiltInRole.StorageBlobDataReader)) }));

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(agent.GetEndpoint("http"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var storageRoles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "storage-roles");
        Assert.Same(storage.Resource, storageRoles.TargetAzureResource);
    }

    [Fact]
    public async Task ReferenceRoleAssignmentAnnotation_ConsumerNotReferencingFrontingResource_GetsNoRole()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");

        var agent = builder.AddContainer("agent", "img:latest")
            .WithHttpEndpoint();
        agent.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(
            storage.Resource,
            new HashSet<RoleDefinition> { new(StorageBuiltInRole.StorageBlobDataReader.ToString(), nameof(StorageBuiltInRole.StorageBlobDataReader)) }));

        // This compute resource does not reference the fronting agent, so it must not be granted any
        // role on the implied storage target.
        var bystander = builder.AddProject<Project>("bystander", launchProfileName: null);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.False(bystander.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out _));
    }

    [Fact]
    public async Task ReferenceRoleAssignmentAnnotation_SameTargetFromTwoDependencies_DedupesRoles()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");

        // Two fronting resources (e.g. two hosted agents on the same Foundry account) each imply the
        // same role on the same target. A consumer referencing both must end up with a single role
        // assignment for that role, otherwise two RoleAssignment bicep resources collide on the same
        // identifier ("{prefix}_{roleName}") and bicep compilation fails.
        var role = new HashSet<RoleDefinition> { new(StorageBuiltInRole.StorageBlobDataReader.ToString(), nameof(StorageBuiltInRole.StorageBlobDataReader)) };

        var agent1 = builder.AddContainer("agent1", "img:latest").WithHttpEndpoint();
        agent1.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(storage.Resource, role));

        var agent2 = builder.AddContainer("agent2", "img:latest").WithHttpEndpoint();
        agent2.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(storage.Resource, role));

        var consumer = builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(agent1.GetEndpoint("http"))
            .WithReference(agent2.GetEndpoint("http"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleAssignmentResource = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "api-roles-storage");
        Assert.Same(storage.Resource, roleAssignmentResource.TargetAzureResource);
        Assert.Same(consumer.Resource, roleAssignmentResource.OwnerResource);

        // The generated bicep must contain exactly one role assignment, not two duplicates of the same role.
        var manifest = await GetManifestWithBicep(roleAssignmentResource, skipPreparer: true);
        var roleAssignmentCount = System.Text.RegularExpressions.Regex.Matches(manifest.BicepText, "Microsoft.Authorization/roleAssignments@").Count;
        Assert.Equal(1, roleAssignmentCount);
    }

    [Fact]
    public async Task ReferenceRoleAssignmentAnnotation_ConsumerWithExplicitRoleAssignment_DoesNotReintroduceSuppressedDefaults()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        // A fronting resource (e.g. a Foundry hosted agent's node app) implies the Reader role on storage.
        var agent = builder.AddContainer("agent", "img:latest").WithHttpEndpoint();
        agent.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(
            storage.Resource,
            new HashSet<RoleDefinition> { new(StorageBuiltInRole.StorageBlobDataReader.ToString(), nameof(StorageBuiltInRole.StorageBlobDataReader)) }));

        // The consumer references storage directly (so its default role assignments would normally be
        // applied) but declares an explicit WithRoleAssignments, which intentionally suppresses those
        // defaults. The implied-reference hook must add only its Reader role and must NOT re-introduce the
        // suppressed defaults - that was the "pit of failure" the union-of-defaults pattern would have caused.
        var consumer = builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDelegator)
            .WithReference(blobs)
            .WithReference(agent.GetEndpoint("http"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleIds = consumer.Resource.Annotations.OfType<RoleAssignmentAnnotation>()
            .Where(a => a.Target == storage.Resource)
            .SelectMany(a => a.Roles)
            .Select(r => r.Id)
            .ToHashSet();

        // Explicit role + implied Reader are present.
        Assert.Contains(StorageBuiltInRole.StorageBlobDelegator.ToString(), roleIds);
        Assert.Contains(StorageBuiltInRole.StorageBlobDataReader.ToString(), roleIds);

        // The suppressed defaults must not be re-introduced by the implied-reference hook.
        Assert.DoesNotContain(StorageBuiltInRole.StorageBlobDataContributor.ToString(), roleIds);
        Assert.DoesNotContain(StorageBuiltInRole.StorageTableDataContributor.ToString(), roleIds);
        Assert.DoesNotContain(StorageBuiltInRole.StorageQueueDataContributor.ToString(), roleIds);
    }

    [Fact]
    public async Task ReferenceRoleAssignmentAnnotation_ConsumerWithDirectReference_KeepsDefaultsAndAddsImpliedRole()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        // A fronting resource (e.g. a Foundry hosted agent's node app) implies the Reader role on storage.
        var agent = builder.AddContainer("agent", "img:latest").WithHttpEndpoint();
        agent.Resource.Annotations.Add(new ReferenceRoleAssignmentAnnotation(
            storage.Resource,
            new HashSet<RoleDefinition> { new(StorageBuiltInRole.StorageBlobDataReader.ToString(), nameof(StorageBuiltInRole.StorageBlobDataReader)) }));

        // The consumer references storage directly without declaring explicit role assignments, so the
        // account defaults still apply. The implied-reference hook must add its Reader role alongside the
        // defaults (the caller - the preparer - owns defaults; the hook never replaces or removes them).
        var consumer = builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(blobs)
            .WithReference(agent.GetEndpoint("http"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roleIds = consumer.Resource.Annotations.OfType<RoleAssignmentAnnotation>()
            .Where(a => a.Target == storage.Resource)
            .SelectMany(a => a.Roles)
            .Select(r => r.Id)
            .ToHashSet();

        // Defaults are preserved by the preparer's normal reference walk.
        Assert.Contains(StorageBuiltInRole.StorageBlobDataContributor.ToString(), roleIds);
        Assert.Contains(StorageBuiltInRole.StorageTableDataContributor.ToString(), roleIds);
        Assert.Contains(StorageBuiltInRole.StorageQueueDataContributor.ToString(), roleIds);

        // The implied Reader role is added on top of the defaults.
        Assert.Contains(StorageBuiltInRole.StorageBlobDataReader.ToString(), roleIds);
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }

    private static Task ExecutePipelineAsync(DistributedApplication app)
    {
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<AzureResourcePreparerTests>>(),
            CancellationToken.None);

        return pipeline.ExecuteAsync(context);
    }

    private static HashSet<RoleDefinition> CreateStorageRoleDefinitions(params StorageBuiltInRole[] roles)
    {
        return [.. roles.Select(role => new RoleDefinition(role.ToString(), StorageBuiltInRole.GetBuiltInRoleName(role)))];
    }

    private static HashSet<RoleDefinition> CreateKeyVaultRoleDefinitions(params KeyVaultBuiltInRole[] roles)
    {
        return [.. roles.Select(role => new RoleDefinition(role.ToString(), KeyVaultBuiltInRole.GetBuiltInRoleName(role)))];
    }
}
