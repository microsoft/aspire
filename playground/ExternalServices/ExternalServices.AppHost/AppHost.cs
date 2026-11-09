// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Yarp.Transforms;

var builder = DistributedApplication.CreateBuilder(args);

var externalServiceUrl = builder.AddParameter("external-service-url")
    .WithDescription("The URL of the external service.")
    .WithCustomInput(p => new()
    {
        Name = "ParameterInput",
        InputType = InputType.Text,
        Value = "https://example.com",
        Label = p.Name,
        Placeholder = $"Enter value for {p.Name}",
        Description = p.Description
    });
var externalService = builder.AddExternalService("external-service", externalServiceUrl);

var nuget = builder.AddExternalService("nuget", "https://api.nuget.org/")
    .WithHttpHealthCheck(path: "/v3/index.json");

var externalGateway = builder.AddYarp("gateway")
    .WithConfiguration(c =>
    {
        var nugetCluster = c.AddCluster(nuget);
        c.AddRoute("/nuget/{**catchall}", nugetCluster).WithTransformPathRemovePrefix("/nuget");
        c.AddRoute("/external-service/{**catchall}", externalService).WithTransformPathRemovePrefix("/external-service");
    });

var frontend = builder.AddDotnetProject("frontend", "../WebFrontEnd/WebFrontEnd.csproj")
       .WithReference(nuget)
       .WithEnvironment("EXTERNAL_SERVICE_URL", externalService)
       .WithReference(externalGateway);

// A container calling back to a host service through a *.dev.localhost URL. HostUrl must translate
// the host name (and port, when the container tunnel is enabled) so the URL is reachable from the container.
builder.AddContainer("hosturl-probe", "curlimages/curl")
       .WithEnvironment("FRONTEND_URL", new HostUrl("http://frontend.dev.localhost:5115"))
       .WithEntrypoint("/bin/sh")
       .WithArgs("-c", "while true; do echo \"$FRONTEND_URL -> $(curl -sS -o /dev/null -w '%{http_code}' \"$FRONTEND_URL\" 2>&1)\"; sleep 5; done")
       .WaitFor(frontend);

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
