// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class ExistingAzureResourceTests
{
    [Fact]
    public async Task AddExistingAzureServiceBusInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var existingResourceName = builder.AddParameter("existingResourceName");
        var serviceBus = builder.AddAzureServiceBus("messaging")
            .RunAsExisting(existingResourceName, resourceGroupParameter: default);
        serviceBus.AddServiceBusQueue("queue");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
              
    }

    [Fact]
    public async Task RequiresPublishAsExistingInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var serviceBus = builder.AddAzureServiceBus("messaging")
            .RunAsExisting(existingResourceName, resourceGroupParameter: default);
        serviceBus.AddServiceBusQueue("queue");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
              
    }

    [Fact]
    public async Task AddExistingAzureServiceBusInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var serviceBus = builder.AddAzureServiceBus("messaging")
            .PublishAsExisting(existingResourceName, resourceGroupParameter: default);
        serviceBus.AddServiceBusQueue("queue");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
              
    }

    [Fact]
    public async Task SupportsExistingServiceBusWithResourceGroupInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var serviceBus = builder.AddAzureServiceBus("messaging")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);
        serviceBus.AddServiceBusQueue("queue");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (manifest, bicep) = await GetManifestWithBicep(model, serviceBus.Resource);

        // ensure the role assignments resource has the correct manifest and bicep, specifically the correct scope property
        var messagingRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "messaging-roles");
        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(messagingRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
                .AppendContentAsFile(bicep, "bicep")
                .AppendContentAsFile(rolesManifest.ToString(), "json")
                .AppendContentAsFile(rolesBicep, "bicep");
    }

    [Fact]
    public async Task SupportsExistingServiceBusWithStaticArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var serviceBus = builder.AddAzureServiceBus("messaging")
            .PublishAsExisting("existingResourceName", "existingResourceGroupName");
        serviceBus.AddServiceBusQueue("queue");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
              
    }

    [Fact]
    public async Task SupportsExistingStorageAccountWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var storageAccount = builder.AddAzureStorage("storage")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(storageAccount.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
              
    }

    [Fact]
    public async Task SupportsExistingStorageAccountWithResourceGroupAndStaticArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storageAccount = builder.AddAzureStorage("storage")
            .PublishAsExisting("existingResourcename", "existingResourceGroupName");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(storageAccount.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAppConfigurationWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var appConfiguration = builder.AddAzureAppConfiguration("appConfig")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(appConfiguration.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingEventHubsWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var eventHubs = builder.AddAzureEventHubs("eventHubs")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(eventHubs.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingKeyVaultWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var keyVault = builder.AddAzureKeyVault("keyVault")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(keyVault.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingLogAnalyticsWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var logAnalytics = builder.AddAzureLogAnalyticsWorkspace("logAnalytics")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(logAnalytics.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingPostgresSqlWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var postgresSql = builder.AddAzurePostgresFlexibleServer("postgresSql")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(postgresSql.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingPostgresSqlWithResourceGroupWithPasswordAuth()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var existingUserName = builder.AddParameter("existingUserName");
        var existingPassword = builder.AddParameter("existingPassword");

        var postgresSql = builder.AddAzurePostgresFlexibleServer("postgresSql")
            .PublishAsExisting(existingResourceName, existingResourceGroupName)
            .WithPasswordAuthentication(existingUserName, existingPassword);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(postgresSql.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureSearchWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var search = builder.AddAzureSearch("search")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(search.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureSignalRWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var signalR = builder.AddAzureSignalR("signalR")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(signalR.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureWebPubSubWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var webPubSub = builder.AddAzureWebPubSub("webPubSub")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(webPubSub.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureSqlServerWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var sqlServer = builder.AddAzureSqlServer("sqlServer")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(sqlServer.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureSqlServerInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var sqlServer = builder.AddAzureSqlServer("sqlServer")
            .RunAsExisting(existingResourceName, resourceGroupParameter: default);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(sqlServer.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    [Obsolete]
    public async Task SupportsExistingAzureRedisWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var redis = builder.AddAzureRedis("redis")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    [Obsolete]
    public async Task SupportsExistingAzureRedisWithResouceGroupAndAccessKeyAuth()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var redis = builder.AddAzureRedis("redis")
            .PublishAsExisting("existingResourceName", "existingResourceGroupName")
            .WithAccessKeyAuthentication();

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureRedisEnterpriseWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var redis = builder.AddAzureManagedRedis("redis")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task SupportsExistingAzureRedisEnterpriseWithResourceGroupAndAccessKeyAuth()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var redis = builder.AddAzureManagedRedis("redis")
            .PublishAsExisting("existingResourceName", "existingResourceGroupName")
            .WithAccessKeyAuthentication();

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task SupportsExistingAzureApplicationInsightsWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var appInsights = builder.AddAzureApplicationInsights("appInsights")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(appInsights.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureOpenAIWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var openAI = builder.AddAzureOpenAI("openAI")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);
        openAI.AddDeployment("mymodel", "gpt-35-turbo", "0613")
            .WithProperties(d =>
            {
                d.SkuName = "Basic";
                d.SkuCapacity = 4;
            });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(openAI.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureCosmosDBWithResourceGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        cosmos.AddCosmosDatabase("mydb")
            .AddContainer("container", "/id");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureCosmosDBWithResourceGroupAccessKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .PublishAsExisting(existingResourceName, existingResourceGroupName)
            .WithAccessKeyAuthentication();

        cosmos.AddCosmosDatabase("mydb")
            .AddContainer("container", "/id");

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(cosmos.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureContainerRegistryInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var acr = builder.AddAzureContainerRegistry("acr")
            .RunAsExisting(existingResourceName, resourceGroupParameter: default);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(acr.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }

    [Fact]
    public async Task SupportsExistingAzureContainerRegistryInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingResourceName = builder.AddParameter("existingResourceName");
        var existingResourceGroupName = builder.AddParameter("existingResourceGroupName");
        var acr = builder.AddAzureContainerRegistry("acr")
            .PublishAsExisting(existingResourceName, existingResourceGroupName);

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(acr.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
            
    }
}
