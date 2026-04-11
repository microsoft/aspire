// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzurePrivateEndpointLockdownTests
{
    [Fact]
    public async Task AddAzureCosmosDB_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var cosmos = builder.AddAzureCosmosDB("cosmos");

        subnet.AddPrivateEndpoint(cosmos);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureSqlServer_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var sql = builder.AddAzureSqlServer("sql");

        subnet.AddPrivateEndpoint(sql);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(sql.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzurePostgresFlexibleServer_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var postgres = builder.AddAzurePostgresFlexibleServer("postgres");

        subnet.AddPrivateEndpoint(postgres);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(postgres.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureManagedRedis_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var redis = builder.AddAzureManagedRedis("redis");

        subnet.AddPrivateEndpoint(redis);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureServiceBus_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var serviceBus = builder.AddAzureServiceBus("servicebus");

        subnet.AddPrivateEndpoint(serviceBus);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureEventHubs_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var eventHubs = builder.AddAzureEventHubs("eventhubs");

        subnet.AddPrivateEndpoint(eventHubs);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(eventHubs.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureKeyVault_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var keyVault = builder.AddAzureKeyVault("keyvault");

        subnet.AddPrivateEndpoint(keyVault);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(keyVault.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureAppConfiguration_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var appConfig = builder.AddAzureAppConfiguration("appconfig");

        subnet.AddPrivateEndpoint(appConfig);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(appConfig.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureSearch_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var search = builder.AddAzureSearch("search");

        subnet.AddPrivateEndpoint(search);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(search.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureSignalR_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var signalR = builder.AddAzureSignalR("signalr");

        subnet.AddPrivateEndpoint(signalR);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(signalR.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureWebPubSub_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var webPubSub = builder.AddAzureWebPubSub("webpubsub");

        subnet.AddPrivateEndpoint(webPubSub);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(webPubSub.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureOpenAI_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var openai = builder.AddAzureOpenAI("openai");

        subnet.AddPrivateEndpoint(openai);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(openai.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddFoundry_WithPrivateEndpoint_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("myvnet");
        var subnet = vnet.AddSubnet("pesubnet", "10.0.1.0/24");
        var foundry = builder.AddFoundry("foundry");

        subnet.AddPrivateEndpoint(foundry);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(foundry.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }
}
