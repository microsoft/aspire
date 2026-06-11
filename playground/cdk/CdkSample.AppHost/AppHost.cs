// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.ServiceBus;
using Azure.Provisioning.Storage;

var builder = DistributedApplication.CreateBuilder(args);

var cosmosdb = builder.AddAzureCosmosDB("cosmos");
cosmosdb.AddCosmosDatabase("cosmosdb");

var sku = builder.AddParameter("storagesku");
var locationOverride = builder.AddParameter("locationOverride");
var storage = builder.AddAzureStorage("storage")
    .ConfigureInfrastructure(infrastructure =>
    {
        var account = infrastructure.GetProvisionableResources().OfType<StorageAccount>().Single();
        account.Sku = new StorageSku() { Name = sku.AsProvisioningParameter(infrastructure) };
        account.Location = locationOverride.AsProvisioningParameter(infrastructure);
    });

var blobs = storage.AddBlobs("blobs");

var sqldb = builder.AddAzureSqlServer("sql").AddDatabase("sqldb");

var signaturesecret = builder.AddParameter("signaturesecret", secret: true);
var keyvault = builder.AddAzureKeyVault("mykv")
    .ConfigureInfrastructure(infrastructure =>
{
    var keyVault = infrastructure.GetProvisionableResources().OfType<KeyVaultService>().Single();
    var secret = new KeyVaultSecret("mysecret")
    {
        Parent = keyVault,
        Name = "mysecret",
        Properties = new SecretProperties { Value = signaturesecret.AsProvisioningParameter(infrastructure) }
    };
    infrastructure.Add(secret);
});

var cache = builder.AddAzureManagedRedis("cache");

var pgsqlAdministratorLogin = builder.AddParameter("pgsqlAdministratorLogin");
var pgsqlAdministratorLoginPassword = builder.AddParameter("pgsqlAdministratorLoginPassword", secret: true);
var pgsqldb = builder.AddAzurePostgresFlexibleServer("pgsql")
                   .WithPasswordAuthentication(pgsqlAdministratorLogin, pgsqlAdministratorLoginPassword)
                   .AddDatabase("pgsqldb");

var pgsql2 = builder.AddAzurePostgresFlexibleServer("pgsql2")
    .AddDatabase("pgsql2db");

var sb = builder.AddAzureServiceBus("servicebus");

sb.AddServiceBusQueue("queue1");
sb.ConfigureInfrastructure(infrastructure =>
 {
     var queue = infrastructure.GetProvisionableResources().OfType<ServiceBusQueue>().Single(q => q.BicepIdentifier == "queue1");
     queue.MaxDeliveryCount = 5;
     queue.LockDuration = TimeSpan.FromMinutes(5);
 });

sb.AddServiceBusTopic("topic1")
    .AddServiceBusSubscription("subscription2");
sb.ConfigureInfrastructure(infrastructure =>
{
    var topic = infrastructure.GetProvisionableResources().OfType<ServiceBusTopic>().Single(q => q.BicepIdentifier == "topic1");
    topic.EnablePartitioning = true;
});

sb.AddServiceBusTopic("topic2")
    .AddServiceBusSubscription("subscription1");
sb.ConfigureInfrastructure(infrastructure =>
{
    var subscription = infrastructure.GetProvisionableResources().OfType<ServiceBusSubscription>().Single(q => q.BicepIdentifier == "subscription1");
    subscription.LockDuration = TimeSpan.FromMinutes(5);
    subscription.RequiresSession = true;
});

var topic3 = sb.AddServiceBusTopic("topic3");
topic3.AddServiceBusSubscription("sub1");
topic3.AddServiceBusSubscription("sub2");

var appConfig = builder.AddAzureAppConfiguration("appConfig");

var search = builder.AddAzureSearch("search");

var signalr = builder.AddAzureSignalR("signalr");

var logAnalyticsWorkspace = builder.AddAzureLogAnalyticsWorkspace("logAnalyticsWorkspace")
    .ConfigureInfrastructure(infrastructure =>
    {
        var logAnalyticsWorkspace = infrastructure.GetProvisionableResources().OfType<OperationalInsightsWorkspace>().Single();
        logAnalyticsWorkspace.Sku = new OperationalInsightsWorkspaceSku()
        {
            Name = OperationalInsightsWorkspaceSkuName.PerNode
        };
    });

var appInsights = builder.AddAzureApplicationInsights("appInsights", logAnalyticsWorkspace)
    .ConfigureInfrastructure(infrastructure =>
    {
        var appInsights = infrastructure.GetProvisionableResources().OfType<ApplicationInsightsComponent>().Single();
        appInsights.IngestionMode = ComponentIngestionMode.LogAnalytics;
    });

builder.AddProject<Projects.CdkSample_ApiService>("api")
    .WithExternalHttpEndpoints()
    .WithReference(signalr)
    .WithReference(blobs)
    .WithReference(sqldb)
    .WithReference(keyvault)
    .WithReference(cache)
    .WithReference(cosmosdb)
    .WithReference(pgsqldb)
    .WithReference(pgsql2)
    .WithReference(sb)
    .WithReference(appConfig)
    .WithReference(search)
    .WithReference(appInsights);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
