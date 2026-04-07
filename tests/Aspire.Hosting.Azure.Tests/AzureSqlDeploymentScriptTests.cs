// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.Utils;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSqlDeploymentScriptTests
{
    [Fact]
    public async Task SqlWithPrivateEndpoint_AutoCreatesBothSubnetAndStorage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");

        var sqlServer = builder.AddAzureSqlServer("sql");
        var db = sqlServer.AddDatabase("db");

        peSubnet.AddPrivateEndpoint(sqlServer);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task SqlWithPrivateEndpoint_ExplicitSubnet_AutoCreatesStorage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var aciSubnet = vnet.AddSubnet("acisubnet", "10.0.2.0/29");

        var sqlServer = builder.AddAzureSqlServer("sql");
        var db = sqlServer.AddDatabase("db");

        peSubnet.AddPrivateEndpoint(sqlServer);
        sqlServer.WithAdminDeploymentScriptSubnet(aciSubnet);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task SqlWithPrivateEndpoint_ExplicitStorage_AutoCreatesSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");

        var sqlServer = builder.AddAzureSqlServer("sql");
        var db = sqlServer.AddDatabase("db");

        peSubnet.AddPrivateEndpoint(sqlServer);

        var storage = builder.AddAzureStorage("depscriptstorage");
        sqlServer.WithAdminDeploymentScriptStorage(storage);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task SqlWithPrivateEndpoint_BothExplicitSubnetAndStorage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var aciSubnet = vnet.AddSubnet("acisubnet", "10.0.2.0/29");

        var sqlServer = builder.AddAzureSqlServer("sql");
        var db = sqlServer.AddDatabase("db");

        peSubnet.AddPrivateEndpoint(sqlServer);
        sqlServer.WithAdminDeploymentScriptSubnet(aciSubnet);

        var storage = builder.AddAzureStorage("depscriptstorage");
        sqlServer.WithAdminDeploymentScriptStorage(storage);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task SqlWithPrivateEndpoint_StorageBeforePrivateEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");

        var sqlServer = builder.AddAzureSqlServer("sql");
        var db = sqlServer.AddDatabase("db");

        // Call WithAdminDeploymentScriptStorage BEFORE AddPrivateEndpoint
        var storage = builder.AddAzureStorage("depscriptstorage");
        sqlServer.WithAdminDeploymentScriptStorage(storage);

        peSubnet.AddPrivateEndpoint(sqlServer);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task SqlWithPrivateEndpoint_SubnetBeforePrivateEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var aciSubnet = vnet.AddSubnet("acisubnet", "10.0.2.0/29");

        var sqlServer = builder.AddAzureSqlServer("sql");
        var db = sqlServer.AddDatabase("db");

        // Call WithAdminDeploymentScriptSubnet BEFORE AddPrivateEndpoint
        sqlServer.WithAdminDeploymentScriptSubnet(aciSubnet);

        peSubnet.AddPrivateEndpoint(sqlServer);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task SqlWithPrivateEndpoint_ClearDefaultRoleAssignments_RemovesDeploymentScriptInfra()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var peSubnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");

        var sqlServer = builder.AddAzureSqlServer("sql")
            .ClearDefaultRoleAssignments();
        var db = sqlServer.AddDatabase("db");

        peSubnet.AddPrivateEndpoint(sqlServer);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db);

        await VerifyAllAzureBicep(builder);
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}

