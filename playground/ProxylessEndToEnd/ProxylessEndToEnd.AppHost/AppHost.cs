// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis", 9999).WithEndpoint("tcp", (endpoint) =>
{
    endpoint.IsProxied = false;
});

builder.AddProject<Projects.ProxylessEndToEnd_ApiService>("api")
    .WithEndpoint("http", e =>
    {
        e.IsProxied = false;
        e.Port = 12345;
    })
    .WithReference(redis);

builder.AddProject<Projects.ProxylessEndToEnd_ApiService>("api2", launchProfileName: null)
    .WithHttpEndpoint(port: 13456)
    .WithReference(redis);

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
