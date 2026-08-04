#pragma warning disable ASPIREEXTENSION001

var builder = DistributedApplication.CreateBuilder(args);

// Bug #15378 repro with a real Azure Functions project. This validates that
// AddAzureFunctionsProject resources still get IDE execution in Visual Studio
// (DEBUG_SESSION_PORT set, no DEBUG_SESSION_INFO) so breakpoints bind.
var funcApp = builder.AddAzureFunctionsProject<Projects.AzureFunctionsService>("azure-functions-service")
    .WithExternalHttpEndpoints();

// Bug #15606/#15647 repro: A standard project resource should get
// IDE execution when DEBUG_SESSION_PORT is set and the extension
// advertises SupportedLaunchConfigurations that include "project"
// (or omits the list entirely, e.g. Visual Studio).
builder.AddProject<Projects.StandardService>("standard-service")
    .WithReference(funcApp)
    .WaitFor(funcApp);

// Bug #15606/#15647 repro (internal fake integration): a ProjectResource subclass
// added via AddResource (not AddProject) with no SupportsDebuggingAnnotation and
// a launch profile. This simulates third-party integrations like AWS Lambda that
// need IDE execution in debug sessions without calling WithDebugSupport.
builder.AddFakeIntegrationProject(
    "fake-integration-library",
    "../FakeIntegrationLibrary/FakeIntegrationLibrary.csproj",
    "Aspire_fake-integration")
    .WithHttpEndpoint(env: "PORT");

// Bug #15378 repro: A project resource with a custom debug type (simulating
// Azure Functions / AWS Lambda) should still get IDE execution in Visual Studio,
// which sets DEBUG_SESSION_PORT but NOT DEBUG_SESSION_INFO.
// WithDebugSupport replaces the default "project" annotation with a custom type,
// exactly as Aspire.Hosting.Azure.Functions does with "azure-functions".
builder.AddProject<Projects.CustomDebugService>("custom-debug-service")
    .WithDebugSupport(
        mode => new CustomLaunchConfiguration { Mode = mode, ProjectPath = "CustomDebugService" },
        "custom-debug-type");

builder.Build().Run();

static class FakeIntegrationProjectResourceExtensions
{
    public static IResourceBuilder<FakeIntegrationProjectResource> AddFakeIntegrationProject(
        this IDistributedApplicationBuilder builder,
        string name,
        string relativeProjectPath,
        string launchProfileName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(relativeProjectPath);
        ArgumentNullException.ThrowIfNull(launchProfileName);

        var projectPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, relativeProjectPath));

        var resourceBuilder = builder
            .AddResource(new FakeIntegrationProjectResource(name))
            .WithAnnotation(new FakeProjectMetadata(projectPath))
            .WithAnnotation(new LaunchProfileAnnotation(launchProfileName));

        return resourceBuilder;
    }
}

internal sealed class FakeIntegrationProjectResource(string name) : ProjectResource(name);

internal sealed class FakeProjectMetadata(string projectPath) : IProjectMetadata
{
    public string ProjectPath { get; } = projectPath;
}

// Simulates a custom launch configuration like AzureFunctionsLaunchConfiguration
// or an AWS Lambda launch configuration.
internal sealed class CustomLaunchConfiguration
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "custom-debug-type";

    [System.Text.Json.Serialization.JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("project_path")]
    public string ProjectPath { get; set; } = string.Empty;
}
