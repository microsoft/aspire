// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

var parameter = builder.AddParameter("val");

AzureBicepResource? temp00 = null;

var bicep1 = builder.AddBicepTemplate("test", "test.bicep")
                   .WithParameter("test", parameter)
                   // This trick verifies the output reference is working regardless of declaration order
                   .WithParameter("p2", () => new BicepOutputReference("val0", temp00!))
                   .WithParameter("values", ["one", "two"]);

var bicep0 = builder.AddBicepTemplateString("test0",
            """
            param location string = ''
            output val0 string = location
            """
            );

temp00 = bicep0.Resource;

var kv = builder.AddAzureKeyVault("kv3");
var appConfig = builder.AddAzureAppConfiguration("appConfig").WithParameter("sku", "standard");
var storage = builder.AddAzureStorage("storage");
                    // .RunAsEmulator();

var blobs = storage.AddBlobs("blob");
var tables = storage.AddTables("table");
var queues = storage.AddQueues("queue");

var sqlServer = builder.AddAzureSqlServer("sql").AddDatabase("db");

var administratorLogin = builder.AddParameter("administratorLogin");
var administratorLoginPassword = builder.AddParameter("administratorLoginPassword", secret: true);
var pg = builder.AddAzurePostgresFlexibleServer("postgres2")
                .WithPasswordAuthentication(administratorLogin, administratorLoginPassword)
                .AddDatabase("db2");

var cosmosDb = builder.AddAzureCosmosDB("cosmos");
cosmosDb.AddCosmosDatabase("db3");

var logAnalytics = builder.AddAzureLogAnalyticsWorkspace("lawkspc");
var appInsights = builder.AddAzureApplicationInsights("ai", logAnalytics);

// To verify that AZD will populate the LAW parameter.
builder.AddAzureApplicationInsights("aiwithoutlaw");

var redis = builder.AddAzureManagedRedis("redis");

var serviceBus = builder.AddAzureServiceBus("sb");

serviceBus.AddServiceBusQueue("queue1");

var topic1 = serviceBus.AddServiceBusTopic("topic1");
topic1.AddServiceBusSubscription("subscription1");
topic1.AddServiceBusSubscription("subscription2");
serviceBus.AddServiceBusTopic("topic2")
    .AddServiceBusSubscription("topic2sub", "subscription1");

var signalr = builder.AddAzureSignalR("signalr");
var webpubsub = builder.AddAzureWebPubSub("wps");
builder.AddProject<Projects.BicepSample_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(sqlServer)
       .WithReference(pg)
       .WithReference(cosmosDb)
       .WithReference(blobs)
       .WithReference(tables)
       .WithReference(queues)
       .WithReference(kv)
       .WithReference(appConfig)
       .WithReference(appInsights)
       .WithReference(redis)
       .WithReference(serviceBus)
       .WithReference(signalr)
       .WithReference(webpubsub)
       .WithEnvironment("bicepValue_test", bicep1.GetOutput("test"))
       .WithEnvironment("bicepValue0", bicep1.GetOutput("val0"))
       .WithEnvironment("bicepValue1", bicep1.GetOutput("val1"));

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
