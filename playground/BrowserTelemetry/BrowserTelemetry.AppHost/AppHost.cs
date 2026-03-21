// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

<<<<<<<< HEAD:playground/BrowserTelemetry/BrowserTelemetry.AppHost/AppHost.cs
#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.BrowserTelemetry_Web>("web")
    .WithExternalHttpEndpoints()
    .WithBrowserLogs();
========
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var builder = DistributedApplication.CreateBuilder(args);

var sqlLocal = builder.AddAzureSqlServer("sqlLocal")
    .RunAsContainer();

var dbLocal = sqlLocal.AddDatabase("dbLocal");

var sqlAzure = builder.AddAzureSqlServer("sqlAzure");
var dbAzure = sqlAzure.AddDatabase("dbAzure");

var sqlNoTls = builder.AddSqlServer("sqlNoTls")
    .WithoutHttpsCertificate();

var dbsetup = builder.AddProject<Projects.SqlServerEndToEnd_DbSetup>("dbsetup")
                     .WithReference(dbLocal).WaitFor(sqlLocal)
                     .WithReference(dbAzure).WaitFor(sqlAzure);

builder.AddProject<Projects.SqlServerEndToEnd_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(dbLocal).WaitFor(dbLocal)
       .WithReference(dbAzure).WaitFor(dbAzure)
       .WaitForCompletion(dbsetup);
>>>>>>>> 8e68a8d71 (Refactor SQL Server setup and connection handling with TLS support):playground/SqlServerEndToEnd/SqlServerEndToEnd.AppHost/AppHost.cs

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

#pragma warning restore ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
