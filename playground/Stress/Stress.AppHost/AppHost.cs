// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

#pragma warning disable ASPIREDOTNETTOOL

var builder = DistributedApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks().AddAsyncCheck("health-test", async (ct) =>
{
    await Task.Delay(5_000, ct);
    return HealthCheckResult.Healthy();
});

var manualArgSource = builder.AddExecutable("manual-arg-source", "dotnet", Environment.CurrentDirectory)
    .WithHttpEndpoint(targetPort: 8088);
var manualArgEndpoint = manualArgSource.GetEndpoint("http");

manualArgSource.WithArgs("--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)));

builder.AddProject<Projects.Stress_Empty>("manual-project-args", launchProfileName: null)
    .WithArgs("--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)));

builder.AddContainer("manual-container-args", "alpine")
    .WithEntrypoint("sleep")
    .WithArgs("3600", "--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)));

builder.AddDotnetTool("manual-dotnet-tool-args", "dotnet-dump")
    .WithArgs("--version", "--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)))
    .WithExplicitStart();

for (var i = 0; i < 2; i++)
{
    var name = $"test-{i:0000}";
    var rb = builder.AddTestResource(name);
    IResource parent = rb.Resource;

    for (var j = 0; j < 3; j++)
    {
        name += $"-n{j}";
        var nestedRb = builder.AddNestedResource(name, parent);
        parent = nestedRb.Resource;
    }
}

builder.AddParameter("testParameterResource", () => "value", secret: true);
var apiKeyParam = builder.AddParameter("api-key", secret: true);
var connectionStringParam = builder.AddParameter("db-connection-string");
builder.AddContainer("hiddenContainer", "alpine")
    .WithHidden()
    .WithInitialState(new CustomResourceSnapshot
    {
        ResourceType = "CustomHiddenContainerType",
        Properties = []
    });

// TODO: OTEL env var can be removed when OTEL libraries are updated to 1.9.0
// See https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/RELEASENOTES.md#1100
var serviceBuilder = builder.AddProject<Projects.Stress_ApiService>("stress-apiservice", launchProfileName: null)
    .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_METRICS_EMIT_OVERFLOW_ATTRIBUTE", "true")
    .WithEnvironment("API_KEY", apiKeyParam)
    .WithEnvironment("DB_CONNECTION_STRING", connectionStringParam)
    .WithIconName("Server");
serviceBuilder
    .WithEnvironment("HOST", $"{serviceBuilder.GetEndpoint("http").Property(EndpointProperty.Host)}")
    .WithEnvironment("PORT", $"{serviceBuilder.GetEndpoint("http").Property(EndpointProperty.Port)}")
    .WithEnvironment("URL", $"{serviceBuilder.GetEndpoint("http").Property(EndpointProperty.Url)}");

serviceBuilder.WithHttpEndpoint(5180, name: $"http");
for (var i = 1; i <= 30; i++)
{
    var port = 5180 + i;
    serviceBuilder.WithHttpEndpoint(port, name: $"http-{port}");
}

var telemetryBuilder = builder.AddProject<Projects.Stress_TelemetryService>("stress-telemetryservice")
       .WithUrls(c => c.Urls.Add(new() { Url = "https://someplace.com", DisplayText = "Some place" }))
       .WithUrl("https://someotherplace.com/some-path", "Some other place")
       .WithUrl("https://extremely-long-url.com/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz//abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmno");

builder.AddCommandResources(serviceBuilder, telemetryBuilder);

builder.OnBeforeStart((@event, ct) =>
{
    InteractionPages.Register(@event.Services);

    return Task.CompletedTask;
});

new TodoInteraction(serviceBuilder).Register(builder);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.AddExecutable("executableWithSingleArg", "dotnet", Environment.CurrentDirectory, "--version");
builder.AddExecutable("executableWithSingleEscapedArg", "dotnet", Environment.CurrentDirectory, "one two");
builder.AddExecutable("executableWithMultipleArgs", "dotnet", Environment.CurrentDirectory, "--version", "one two");

IResourceBuilder<IResource>? previousResourceBuilder = null;

for (var i = 0; i < 3; i++)
{
    var resourceBuilder = builder.AddProject<Projects.Stress_Empty>($"empty-{i:0000}", launchProfileName: null)
                                .WithIconName("Document");
    if (previousResourceBuilder != null)
    {
        resourceBuilder.WaitFor(previousResourceBuilder);
        resourceBuilder.WithHealthCheck("health-test");
    }

    previousResourceBuilder = resourceBuilder;
}

builder.AddProject<Projects.Stress_Empty>("empty-profile-1", launchProfileName: "Profile1");
builder.AddProject<Projects.Stress_Empty>("empty-profile-2", launchProfileName: "Profile1")
    .WithEnvironment("APPHOST_ENV_VAR", "test")
    .WithEnvironment("ENV_TO_OVERRIDE", "this value came from the apphost")
    .WithArgs("arg_from_apphost");

builder.AddNoStatusResource("no-status-resource");

builder.Build().Run();

