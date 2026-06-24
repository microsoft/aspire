#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

builder.AddProject<Projects.SandboxProject_ApiService>("api")
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints()
    .WithEnvironment("SANDBOX_PROJECT_VALUE", "project-resource")
    .PublishAsSandbox(sandboxGroup, new AzureSandboxOptions
    {
        Cpu = "1000m",
        Memory = "2048Mi",
        Disk = "20480Mi"
    });

builder.Build().Run();
