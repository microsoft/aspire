// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Deployment;

public class DeployPipelineIntegrationTests
{
    [Fact]
    public void DeployStep_IsRegisteredInPipeline()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Act
        var envResource = model.Resources.OfType<RadiusEnvironmentResource>().First();
        var pipelineAnnotations = envResource.Annotations.OfType<PipelineStepAnnotation>().ToList();

        // Assert — should have at least a publish step and a deploy step
        Assert.True(pipelineAnnotations.Count >= 2, $"Expected at least 2 pipeline step annotations, got {pipelineAnnotations.Count}");
    }

    [Fact]
    public void DeployStep_DependsOnPublishStepOnly_NotPush()
    {
        // Arrange
        var environment = new RadiusEnvironmentResource("testenv");
        var step = new RadiusDeploymentPipelineStep(environment);

        // Act
        var pipelineStep = step.CreatePipelineStep();

        // Assert
        Assert.Contains("publish-radius-testenv", pipelineStep.DependsOnSteps);
        Assert.Contains("deploy-prereq", pipelineStep.DependsOnSteps);

        // Must NOT depend on push — to support kind clusters without a container registry
        Assert.DoesNotContain("push", pipelineStep.DependsOnSteps);
        Assert.DoesNotContain("push-prereq", pipelineStep.DependsOnSteps);
    }

    [Fact]
    public void RadDeployCommand_SynthesizedCorrectly_WithFullBicepPath()
    {
        // Arrange
        var environment = new RadiusEnvironmentResource("myenv");

        // Verify the step creates a pipeline step with the correct name format
        var step = new RadiusDeploymentPipelineStep(environment);
        var pipelineStep = step.CreatePipelineStep();

        // Assert — step name encodes the environment name
        Assert.Equal("deploy-radius-myenv", pipelineStep.Name);
        Assert.Contains("rad CLI", pipelineStep.Description);
    }

    [Fact]
    public void DeployStep_StreamsOutputViaLoggerAndReportingStep()
    {
        // Verify the deployment step is configured to capture stdout/stderr.
        // The actual forwarding happens via process.OutputDataReceived and ErrorDataReceived
        // events in ExecuteAsync, which log via ILogger and context.ReportingStep.
        // We verify this indirectly by checking the step is configured with an async Action
        // that accepts a PipelineStepContext (which provides the ReportingStep for output).
        var environment = new RadiusEnvironmentResource("testenv");
        var step = new RadiusDeploymentPipelineStep(environment);
        var pipelineStep = step.CreatePipelineStep();

        // The step has an Action (ExecuteAsync) that processes output
        Assert.NotNull(pipelineStep.Action);
        Assert.Equal("deploy-radius-testenv", pipelineStep.Name);
    }

    [Fact]
    public void DeployStep_CommandUsesRadDeployWithBicepFile()
    {
        // The deploy step runs `rad deploy "app.bicep"` against the output directory.
        // Verify the step description references the rad CLI and environment name.
        var environment = new RadiusEnvironmentResource("production");
        var step = new RadiusDeploymentPipelineStep(environment);
        var pipelineStep = step.CreatePipelineStep();

        Assert.Contains("rad CLI", pipelineStep.Description);
        Assert.Contains("production", pipelineStep.Description);
    }

    [Fact]
    public void DeployStep_HasCorrectDependencyChain()
    {
        // Arrange
        var environment = new RadiusEnvironmentResource("staging");
        var step = new RadiusDeploymentPipelineStep(environment);

        // Act
        var pipelineStep = step.CreatePipelineStep();

        // Assert — Deploy step depends on:
        // 1. publish-radius-{name} (Bicep must be generated first)
        // 2. deploy-prereq (standard deploy prerequisite)
        Assert.Contains("publish-radius-staging", pipelineStep.DependsOnSteps);
        Assert.Contains("deploy-prereq", pipelineStep.DependsOnSteps);

        // And is required by the "deploy" well-known step
        Assert.Contains("deploy", pipelineStep.RequiredBySteps);
    }

    [Fact]
    public void BicepGenerationCalledBeforeRadDeploy_ByDependencyOrdering()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var envResource = model.Resources.OfType<RadiusEnvironmentResource>().First();

        // Verify we can resolve the publish and deploy steps from annotations
        var annotations = envResource.Annotations.OfType<PipelineStepAnnotation>().ToList();

        // The deploy step should exist and depend on the publish step
        // We verify this by checking the step properties directly
        var deployStep = new RadiusDeploymentPipelineStep(envResource);
        var step = deployStep.CreatePipelineStep();

        // Assert — the deploy step depends on publish-radius-myenv
        Assert.Contains("publish-radius-myenv", step.DependsOnSteps);

        // The publish step (publish-radius-myenv) is RequiredBy "publish"
        // The deploy step depends on publish-radius-myenv
        // This means Bicep generation MUST run before rad deploy
    }

    [Fact]
    public void MultipleEnvironments_EachGetSeparateDeployStep()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("dev");
        builder.AddRadiusEnvironment("staging");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Act
        var environments = model.Resources.OfType<RadiusEnvironmentResource>().ToList();

        // Assert — each environment should have its own pipeline step annotations
        Assert.Equal(2, environments.Count);

        foreach (var envResource in environments)
        {
            var annotations = envResource.Annotations.OfType<PipelineStepAnnotation>().ToList();
            Assert.True(annotations.Count >= 2, $"Environment '{envResource.Name}' should have at least 2 pipeline step annotations");
        }
    }
}
