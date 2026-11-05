// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only
#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class JavaScriptPublishValidationTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployGraphRequiresJavaScriptAndNextJsValidationBeforeImageBuilds(bool appService)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "inspect");
        if (appService)
        {
            builder.AddAzureAppServiceEnvironment("env");
        }
        else
        {
            builder.AddAzureContainerAppEnvironment("env");
        }

        builder.AddJavaScriptApp("js", workspace.Path)
            .PublishAsPackageScript("start")
            .WithHttpEndpoint(port: 3001)
            .WithExternalHttpEndpoints();
        builder.AddNextJsApp("nextjs", workspace.Path).WithExternalHttpEndpoints();

        IReadOnlyList<PipelineStep> graph = [];
        builder.Pipeline.AddStep("inspect", _ => Task.CompletedTask);
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            graph = context.Steps.ToArray();
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
            new PipelineContext(
                app.Services.GetRequiredService<DistributedApplicationModel>(),
                app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                app.Services,
                NullLogger.Instance,
                TestContext.Current.CancellationToken));

        Assert.True(Reaches(WellKnownPipelineSteps.Deploy, "build-js"));
        Assert.True(Reaches(WellKnownPipelineSteps.Deploy, "build-nextjs"));
        Assert.True(Reaches("build-js", "validate-javascript-dockerfile-run-script-js"));
        Assert.True(Reaches("build-nextjs", "nextjs-standalone-check-nextjs"));
        Assert.True(Reaches(WellKnownPipelineSteps.Deploy, "validate-javascript-dockerfile-run-script-js"));
        Assert.True(Reaches(WellKnownPipelineSteps.Deploy, "nextjs-standalone-check-nextjs"));

        bool Reaches(string from, string target)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool Visit(string name) => name == target || (visited.Add(name) &&
                graph.Single(step => step.Name == name).DependsOnSteps.Any(Visit));

            return Visit(from);
        }
    }
}
