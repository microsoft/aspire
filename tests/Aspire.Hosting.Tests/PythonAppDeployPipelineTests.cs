// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Python;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "6")]
public class PythonAppDeployPipelineTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AzureContainerAppsDeployValidatesExistingDockerfileBeforeBuildingImage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appDirectory = workspace.CreateDirectory("api").FullName;
        File.WriteAllText(Path.Combine(appDirectory, "main.py"), "print('Hello from Python!')");
        File.WriteAllText(Path.Combine(appDirectory, "Dockerfile"), "FROM scratch\nENTRYPOINT [\"python\", \"main.py\"]\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        builder.AddPythonApp("api", appDirectory, "main.py")
            .WithEntrypoint(EntrypointType.Module, "mymodule");

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<PythonAppDeployPipelineTests>>(),
            CancellationToken.None);

        var steps = (await pipeline.ResolveStepsAsync(context)).ToDictionary(step => step.Name, StringComparer.Ordinal);

        // Azure Container Apps selects each resource's image build step directly, not through the aggregate Build step.
        var deployStepNames = DistributedApplicationPipeline.ComputeTransitiveDependencies(steps[WellKnownPipelineSteps.Deploy], steps)
            .Select(step => step.Name)
            .ToList();

        Assert.Contains("build-api", deployStepNames);
        Assert.Contains("validate-python-dockerfile-entrypoint-api", deployStepNames);
    }
}
