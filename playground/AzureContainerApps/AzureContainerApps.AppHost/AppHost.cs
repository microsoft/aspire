// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREACADOMAINS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Azure;
using Azure.Provisioning.Storage;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("infra");

var customDomain = builder.AddParameter("customDomain");
var certificateName = builder.AddParameter("certificateName");

// Testing secret parameters
var param = builder.AddParameter("secretparam", "fakeSecret", secret: true);

// Testing volumes
var redis = builder.AddRedis("cache")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume();

// Testing secret outputs
var cosmosDb = builder.AddAzureCosmosDB("account")
                      .WithAccessKeyAuthentication()
                      .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent));

cosmosDb.AddCosmosDatabase("db");

// Testing a connection string
var storage = builder.AddAzureStorage("storage")
                     .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("blobs");

// Testing docker files

builder.AddDockerfile("pythonapp", "AppWithDocker");

// Testing projects
builder.AddProject<Projects.AzureContainerApps_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(blobs)
       .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor)
       .WithReference(redis)
       .WithReference(cosmosDb)
       .WithEnvironment("VALUE", param)
       .WithEnvironment(context =>
       {
           if (context.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var identity))
           {
               context.EnvironmentVariables["AZURE_PRINCIPAL_NAME"] = identity.IdentityResource.PrincipalName;
           }
       })
       .PublishAsAzureContainerApp((module, app) =>
       {
           app.ConfigureCustomDomain(customDomain, certificateName);

           // Scale to 0
           app.Template.Scale.MinReplicas = 0;
       });

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
