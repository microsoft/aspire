// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.DevTunnels_ApiService>("api");
var frontend = builder.AddProject<Projects.DevTunnels_WebFrontEnd>("frontend");

var publicDevTunnel = builder.AddDevTunnel("devtunnel-public")
    .WithAnonymousAccess() // All ports on this tunnel default to allowing anonymous access
    .WithReference(frontend.GetEndpoint("https"));

var privateDevTunnel = builder.AddDevTunnel("devtunnel")
    .WithReference(frontend.GetEndpoint("https"))
    .WithReference(api.GetEndpoint("https"));

// Use the GetEndpoint API to get the tunnel endpoint for the API service
var devTunnelPortEndpoint = privateDevTunnel.GetEndpoint(api, "https");

// Inject the private dev tunnel endpoint for API into the frontend service
frontend.WithEnvironment("TUNNEL_URL", devTunnelPortEndpoint);

// Inject the public dev tunnel endpoint for frontend into the API service
api.WithReference(frontend, publicDevTunnel);

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
