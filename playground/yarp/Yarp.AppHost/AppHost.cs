// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Yarp.Transforms;

var builder = DistributedApplication.CreateBuilder(args);

var backendService = builder.AddProject<Projects.Yarp_Backend>("backend");

var frontendService = builder.AddProject<Projects.Yarp_Frontend>("frontend");

var gateway = builder.AddYarp("gateway")
                     .WithConfiguration(yarp =>
                     {
                         yarp.AddRoute(frontendService);
                         yarp.AddRoute("/api/{**catch-all}", backendService)
                             .WithTransformPathRemovePrefix("/api");
                     });

var staticGateway = builder.AddYarp("static-gateway")
                           .WithStaticFiles(Path.Combine("..", "static-content"))
                           .WithConfiguration(yarp =>
                           {
                               yarp.AddRoute("/api/{**catch-all}", backendService)
                                   .WithTransformPathRemovePrefix("/api");
                           });

frontendService.WithReference(gateway);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

var app = builder.Build();

await app.RunAsync();
