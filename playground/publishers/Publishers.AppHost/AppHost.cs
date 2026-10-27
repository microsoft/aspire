// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration.AddCommandLine(args, new Dictionary<string, string> { ["--target"] = "Deployment:Target" });

var target = builder.Configuration["Deployment:Target"];
var publisher = builder.ExecutionContext.PublisherName;

IResourceBuilder<IComputeEnvironmentResource>? environment = (publisher, target) switch
{
    ("default", "kube") => builder.AddKubernetesEnvironment("env"),
    ("default", "azure") => builder.AddAzureContainerAppEnvironment("env"),
    (_, "publish-test") => builder.AddPublishTestResource("env"),
    ("default", _) => builder.AddDockerComposeEnvironment("env"),
    _ => null
};

var param0 = builder.AddParameter("param0");
var param1 = builder.AddParameter("param1", secret: true);
var param2 = builder.AddParameter("param2", "default", publishValueAsDefault: true);
var param3 = builder.AddParameter("param3", "default"); // Runtime only default value.

var azpgdb = builder.AddAzurePostgresFlexibleServer("azpg")
                    .RunAsContainer()
                    .AddDatabase("azdb");

var db = builder.AddPostgres("pg").AddDatabase("db");

var dbsetup = builder.AddDotnetProject("dbsetup", @"..\Publishers.DbSetup\Publishers.DbSetup.csproj")
                     .WithReference(db).WaitFor(db);

var backend = builder.AddDotnetProject("api", @"..\Publishers.ApiService\Publishers.ApiService.csproj")
                     .WithExternalHttpEndpoints()
                     .WithHttpHealthCheck("/health")
                     .WithReference(db).WaitFor(db)
                     .WithReference(azpgdb).WaitFor(azpgdb)
                     .WaitForCompletion(dbsetup)
                     .WithReplicas(2);

// need a container to test.
var sqlServer = builder.AddSqlServer("sqlserver")
        .WithDataVolume("sqlserver-data");

var sqlDb = sqlServer.AddDatabase("sqldb");

builder.AddDotnetProject("frontend", @"..\Publishers.Frontend\Publishers.Frontend.csproj")
       .WithReference(sqlDb)
       .WithEnvironment("P0", param0)
       .WithEnvironment("P1", param1)
       .WithEnvironment("P2", param2)
       .WithEnvironment("P3", param3)
       .WithReference(backend).WaitFor(backend);

builder.AddDockerfile("mycontainer", "qots");

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddDotnetProject(KnownResourceNames.AspireDashboard, @"..\..\..\src\Aspire.Dashboard\Aspire.Dashboard.csproj");
#endif

builder.Build().Run();
