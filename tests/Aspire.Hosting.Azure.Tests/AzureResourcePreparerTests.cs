// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureResourcePreparerTests
{
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
            var storageRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "storage-roles");

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
            var storageRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "storage-roles");

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

        var roleAssignmentResource = Assert.Single(model.Resources.OfType<AzureBicepResource>(), r => r.Name == "api-roles-storage");
        var deploymentTarget = Assert.IsAssignableFrom<AzureBicepResource>(api.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget);

        Assert.Contains(roleAssignmentResource, deploymentTarget.References);
        Assert.Contains(blobPE.Resource, deploymentTarget.References);
        Assert.Contains(queuesPE.Resource, deploymentTarget.References);
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
            n => Assert.Equal("env", n),
            n => Assert.Equal("storage", n),
            n => Assert.Equal("blobs", n),
            n => Assert.Equal("api", n),
            n => Assert.Equal("api2", n),
            n => Assert.Equal("api-identity", n),
            n => Assert.Equal("api-roles-storage", n));
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
            n => Assert.Equal("env", n),
            n => Assert.Equal("storage", n),
            n => Assert.Equal("blobs", n),
            n => Assert.Equal("api", n),
            n => Assert.Equal("frontend", n),
            n => Assert.Equal("api-identity", n),
            n => Assert.Equal("api-roles-storage", n));

        // The ViteApp should NOT get a managed identity since it is a BuildOnlyContainer resource,
        // even though it references the storage account. Only the API should get a managed identity.
        Assert.DoesNotContain(model.Resources, r => r.Name == "frontend-identity");
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
