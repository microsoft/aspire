// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var pg = builder.AddAzurePostgresFlexibleServer("pg")
                .WithPasswordAuthentication()
                .RunAsContainer(c =>
                {
                    c.WithPgAdmin(c =>
                    {
                        c.WithHostPort(15551);
                    });
                });

var db = pg.AddDatabase("db");

// A second database on the same server. Waiting on both databases also waits on their shared
// parent server; that wait (and its log messages) should only happen once.
var auditDb = pg.AddDatabase("auditdb");

var dbsetup = builder.AddDotnetProject("dbsetup", "../WaitForSandbox.DbSetup/WaitForSandbox.DbSetup.csproj")
                     .WithReference(db).WaitFor(db)
                     .WithReference(auditDb).WaitFor(auditDb);

var backend = builder.AddDotnetProject("api", "../WaitForSandbox.ApiService/WaitForSandbox.ApiService.csproj")
                     .WithExternalHttpEndpoints()
                     .WithHttpHealthCheck("/health")
                     .WithReference(db).WaitFor(db)
                     .WaitForCompletion(dbsetup)
                     .WithReplicas(2);

builder.AddDotnetProject("frontend", "../WaitFor.Frontend/WaitFor.Frontend.csproj")
       .WithReference(backend).WaitFor(backend);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddDotnetProject(KnownResourceNames.AspireDashboard, "../../../src/Aspire.Dashboard/Aspire.Dashboard.csproj");
#endif

builder.Build().Run();
