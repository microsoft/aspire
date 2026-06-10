// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddAzurePostgresFlexibleServer("pg")
                .WithPasswordAuthentication()
                .RunAsContainer(c =>
                {
                    c.WithPgAdmin(c =>
                    {
                        c.WithHostPort(15551);
                    });
                })
                .AddDatabase("db");

var dbsetup = builder.AddProject<Projects.WaitForSandbox_DbSetup>("dbsetup")
                     .WithReference(db).WaitFor(db);

var backend = builder.AddProject<Projects.WaitForSandbox_ApiService>("api")
                     .WithExternalHttpEndpoints()
                     .WithHttpHealthCheck("/health")
                     .WithReference(db).WaitFor(db)
                     .WaitForCompletion(dbsetup)
                     .WithReplicas(2);

builder.AddProject<Projects.WaitFor_Frontend>("frontend")
       .WithReference(backend).WaitFor(backend);

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
