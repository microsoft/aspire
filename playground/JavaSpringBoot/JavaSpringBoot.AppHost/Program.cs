// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddJavaApp("springboot", "..")
    .WithMavenGoal("spring-boot:run")
    .WithHttpEndpoint(env: "SERVER_PORT")
    .WithOtelAgent(Path.Combine(builder.AppHostDirectory, "opentelemetry-javaagent.jar"))
    .WithHttpHealthCheck("/actuator/health");

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
