// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Azure.Tests;

public class OutputDiffingOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentlySelectedPublisherWaitsForBaselineAndPreservesPreparation(bool outputDiffing)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "inspect");
        builder.AddAzureAppServiceEnvironment("env");
        if (outputDiffing)
        {
            builder.WithOutputDiffing();
        }
        IReadOnlyList<PipelineStep> graph = [];
        builder.Pipeline.AddStep("inspect", _ => Task.CompletedTask);
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            graph = context.Steps.ToArray();
            return Task.CompletedTask;
        });
        using var app = builder.Build();
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
            new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
                app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
                NullLogger.Instance, TestContext.Current.CancellationToken));

        // Traverse the publisher's own closure, without selecting the comparison target or running the publisher.
        Assert.Equal(outputDiffing, Reaches("publish-azure-environment", "publish-and-diff-baseline"));
        Assert.False(Reaches("publish-azure-environment", "publish-and-diff"));
        Assert.Equal(outputDiffing, Reaches(WellKnownPipelineSteps.BeforeStart, "publish-and-diff-baseline"));
        Assert.True(Reaches(WellKnownPipelineSteps.BeforeStart, "prepare-azure-app-service-env"));
        Assert.True(Reaches("prepare-azure-app-service-env", WellKnownPipelineSteps.ValidateComputeEnvironments));
        Assert.True(Reaches("prepare-azure-app-service-env", "azure-prepare-resources"));
        Assert.True(Reaches("validate-appservice-config-env", WellKnownPipelineSteps.PublishPrereq));

        bool Reaches(string from, string target)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool Visit(string name) => name == target || (visited.Add(name) &&
                graph.Single(step => step.Name == name).DependsOnSteps.Any(Visit));
            return Visit(from);
        }
    }
}

#pragma warning restore ASPIREPIPELINES005
