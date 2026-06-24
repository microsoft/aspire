#pragma warning disable ASPIREJAVASCRIPT001
#pragma warning disable ASPIREAZURE001

var builder = DistributedApplication.CreateBuilder(args);

var appService = builder.AddAzureAppServiceEnvironment("appservice");
var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

var backend = builder.AddProject<Projects.SandboxAppServiceMixed_BackendService>("backend", launchProfileName: null)
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints()
    .WithComputeEnvironment(appService);

var frontend = builder.AddViteApp("frontend", "../SandboxAppServiceMixed.Frontend")
    .WithEnvironment("API_BASE_URL", backend.GetEndpoint("http"))
    .WithExternalHttpEndpoints()
    .PublishAsSandbox(sandboxGroup);

if (builder.ExecutionContext.IsPublishMode)
{
    frontend.WithEndpoint("http", endpoint => endpoint.TargetPort = 8080, createIfNotExists: true);
}

builder.Build().Run();
