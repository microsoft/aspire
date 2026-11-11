// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Azure environment resources are experimental.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.EntraId.Tests;

/// <summary>
/// Publishes the Aspire starter application with Microsoft Entra ID added: a protected API and a web front end that signs users in
/// and calls the API on their behalf. The deployment end-to-end test in Aspire.Deployment.EndToEnd.Tests deploys the same AppHost.
/// </summary>
public class EntraIdStarterScenarioPublishTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task Publish_StarterWithEntra_ConfiguresTheApiAndTheWebFrontEnd()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var apiProject = CreateProject(workspace.Path, "aspire-entra.ApiService", 5101, 7101);
        var webProject = CreateProject(workspace.Path, "aspire-entra.Web", 5102, 7102);
        var outputPath = Path.Combine(workspace.Path, "out");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, outputPath);

        // The AppHost of the scenario. The project paths are absolute only so that the test doesn't need the starter's directory layout.
        builder.AddAzureContainerAppEnvironment("env");

        var apiEntra = builder.AddEntraIdApplication("apiservice-entra")
            .WithScope("access_as_user", "Access the weather API on behalf of the signed-in user");

        var apiService = builder.AddDotnetProject("apiservice", apiProject)
            .WithReference(apiEntra)
            .WithHttpHealthCheck("/health");

        var webEntra = builder.AddEntraIdApplication("webfrontend-entra")
            .WithApiPermissions(apiEntra, "access_as_user")
            .PublishWithFederatedManagedIdentityCredential();

        var webFrontend = builder.AddDotnetProject("webfrontend", webProject)
            .WithExternalHttpEndpoints()
            .WithHttpHealthCheck("/health")
            .WithReference(webEntra, connectionName: null, downstreamApisPrefix: "MyDownstreamApis")
            .WithReference(apiService)
            .WaitFor(apiService);

        webEntra.WithRedirectUris(webFrontend.GetEndpoint("https"), "/signin-oidc", "/signout-callback-oidc");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing the starter with Entra");
        var context = new AzurePublishingContext(
            outputPath,
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value,
            app.Services,
            NullLogger<AzurePublishingContext>.Instance,
            step);

        await context.WriteModelAsync(model, environment);

        // Both registrations, then the two container apps, whose environment variables are what the applications read at runtime.
        await Verify(await File.ReadAllTextAsync(Path.Combine(outputPath, "main.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(outputPath, "apiservice-entra", "apiservice-entra.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(outputPath, "webfrontend-entra", "webfrontend-entra.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(outputPath, "apiservice", "apiservice.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(outputPath, "webfrontend", "webfrontend.bicep")), "bicep");
    }

    // A minimal web project with the launch profiles of the starter template, which give the project its http and https endpoints.
    private static string CreateProject(string root, string name, int httpPort, int httpsPort)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "src", name)).FullName;
        var projectPath = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.Combine(directory, "Properties"));
        File.WriteAllText(Path.Combine(directory, "Properties", "launchSettings.json"), $$"""
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:{{httpPort}}"
                },
                "https": {
                  "commandName": "Project",
                  "applicationUrl": "https://localhost:{{httpsPort}};http://localhost:{{httpPort}}"
                }
              }
            }
            """);
        return projectPath;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
