#pragma warning disable ASPIREAZURE001

var builder = DistributedApplication.CreateBuilder(args);

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
var containerApps = builder.AddAzureContainerAppEnvironment("aca");

builder.AddNodeApp("frontend", "../frontend", "src/server.ts")
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .WithComputeEnvironment(containerApps)
    .WithReference(sandboxGroup)
    .PublishAsDockerFile();

builder.Build().Run();
