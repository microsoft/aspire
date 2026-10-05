// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

// Gives `aspire publish` somewhere to publish to. Each Quarkus resource turns into a container image
// built from a Dockerfile that Aspire generates, which stages the fast-JAR layout the Quarkus build
// produces under target/quarkus-app.
builder.AddDockerComposeEnvironment("compose");

// Gradle, detected from build.gradle in the directory. AddQuarkusApp builds the application, runs it in
// dev mode so live coding works, and declares an HTTP endpoint through QUARKUS_HTTP_PORT.
//
// There is no WithOtelAgent call anywhere in this file: the quarkus-opentelemetry extension is compiled
// into both applications, and AddQuarkusApp points that extension at the Aspire dashboard.
var pricing = builder.AddQuarkusApp("pricing", "../pricing")
    // /q/health is served by the smallrye-health extension, which both services depend on.
    .WithHttpHealthCheck("/q/health")
    .WithExternalHttpEndpoints();

// Maven, detected from the pom.xml in the directory. Identical AppHost code to the Gradle service above,
// which is the point: the build tool is a property of the project, not something the AppHost restates.
builder.AddQuarkusApp("inventory", "../inventory")
    .WithHttpHealthCheck("/q/health")
    .WithExternalHttpEndpoints()
    // Projects the pricing endpoint as services__pricing__http__0, which inventory's
    // application.properties feeds into the MicroProfile REST client's base URL.
    .WithReference(pricing)
    .WaitFor(pricing);

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
