// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning.Storage;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureAppServiceEnvironment("infra").WithDeploymentSlot("staging");

// Testing secret parameters
var param = builder.AddParameter("secretparam", "fakeSecret", secret: true);

// Testing kv secret refs
var cosmosDb = builder.AddAzureCosmosDB("account")
                      .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent));

cosmosDb.AddCosmosDatabase("db");

// Testing managed identity
var storage = builder.AddAzureStorage("storage")
                     .ConfigureInfrastructure(infra =>
                     {
                         var storage = infra.GetProvisionableResources().OfType<StorageAccount>().Single();
                         storage.AllowBlobPublicAccess = false;
                     })
                     .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("blobs");

// Testing projects
builder.AddProject<Projects.AzureAppService_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(blobs)
       .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor)
       .WithReference(cosmosDb)
       .WithEnvironment("VALUE", param)
       .WithEnvironment(context =>
       {
           if (context.Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var identity))
           {
               context.EnvironmentVariables["AZURE_PRINCIPAL_NAME"] = identity.IdentityResource.PrincipalName;
           }
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
