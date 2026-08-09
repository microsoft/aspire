// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("docker-compose");

// Just for validating parameter handling in Dockerfile builds.
var goVersion = builder.AddParameter("goversion", "1.22");
var secret = builder.AddParameter("secret", secret: true);

builder.AddDockerfile("mycontainer", "qots")
       .WithBuildArg("GO_VERSION", goVersion)
       .WithBuildSecret("SECRET_ASENV", secret)
       .WithEnvironment("DOCKER_BUILDKIT", "1");

// Example: Dynamic Dockerfile generation with sync factory
builder.AddContainer("dynamic-sync", "dynamic-sync-image")
    .WithDockerfileFactory("qots", context =>
    {
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"""
            FROM mcr.microsoft.com/oss/go/microsoft/golang:1.23 AS builder
            WORKDIR /app
            COPY . .
            RUN echo "Built at {timestamp}" > /build-info.txt
            RUN go build -o qots .
            
            FROM mcr.microsoft.com/cbl-mariner/base/core:2.0
            COPY --from=builder /app/qots /qots
            COPY --from=builder /build-info.txt /build-info.txt
            ENTRYPOINT ["/qots"]
            """;
    });

// Example: Dynamic Dockerfile generation with async factory
builder.AddContainer("dynamic-async", "dynamic-async-image")
    .WithDockerfileFactory("qots", async context =>
    {
        // Simulate reading from a template or external source
        await Task.Delay(1, context.CancellationToken);
        var baseImage = Environment.GetEnvironmentVariable("BASE_IMAGE") ?? "mcr.microsoft.com/oss/go/microsoft/golang:1.23";
        return $"""
            FROM {baseImage} AS builder
            WORKDIR /app
            COPY . .
            RUN go build -o qots .
            
            FROM mcr.microsoft.com/cbl-mariner/base/core:2.0
            COPY --from=builder /app/qots /qots
            ENTRYPOINT ["/qots"]
            """;
    });

builder.AddRedis("builder-sync")
    .WithImageRegistry("netaspireci.azurecr.io")
    .WithDockerfileBuilder(".", context =>
    {
        if (!context.Resource.TryGetContainerImageName(useBuiltImage: false, out var imageName))
        {
            throw new InvalidOperationException("Container image name not found.");
        }

        context.Builder.From(imageName);
    });

builder.AddRedis("builder-async")
    .WithImageRegistry("netaspireci.azurecr.io")
    .WithDockerfileBuilder(".", async context =>
    {
        await Task.Delay(1, context.CancellationToken);

        if (!context.Resource.TryGetContainerImageName(useBuiltImage: false, out var imageName))
        {
            throw new InvalidOperationException("Container image name not found.");
        }

        context.Builder.From(imageName);
    });

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
