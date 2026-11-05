// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);
var replaceApi = builder.Configuration["COMPOSE_REPLACE_API"] == "true";

await builder.AddDockerComposeFileAsync("legacy", "../compose.yaml", options =>
{
    if (replaceApi)
    {
        var api = builder.AddProject<Projects.ComposeAdoption_Api>("local-api", launchProfileName: null)
            .WithHttpEndpoint(name: "port8080", env: "ASPNETCORE_HTTP_PORTS");
        options.ReplaceService("api", api);
    }
});

builder.Build().Run();
