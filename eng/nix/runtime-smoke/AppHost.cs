// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration["ASPIRE_ENABLE_CONTAINER_TUNNEL"] = "true";

var service = builder.AddDotnetProject("service", "../NixSmoke.Service/NixSmoke.Service.csproj",
        options => options.ExcludeLaunchProfile = true)
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/");

var probeName = Environment.GetEnvironmentVariable("NIX_SMOKE_PROBE_NAME")
    ?? throw new InvalidOperationException("NIX_SMOKE_PROBE_NAME is required.");
builder.AddContainer("probe", "curlimages/curl", "8.12.1")
    .WithContainerName(probeName)
    .WithEnvironment("SERVICE_URL", service.GetEndpoint("http"))
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "trap 'exit 0' TERM INT; while :; do sleep 1; done")
    .WaitFor(service);

builder.Build().Run();
