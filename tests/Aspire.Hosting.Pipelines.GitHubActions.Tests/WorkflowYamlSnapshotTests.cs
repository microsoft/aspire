// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines.GitHubActions.Yaml;

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

[Trait("Partition", "4")]
public class WorkflowYamlSnapshotTests
{
    [Fact]
    public Task BareWorkflow_SingleDefaultJob()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = new PipelineStep
        {
            Name = "build-app",
            Action = _ => Task.CompletedTask
        };

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);
        var output = WorkflowYamlSerializer.Serialize(yaml);

        return Verify(output).UseDirectory("Snapshots");
    }

    [Fact]
    public Task TwoJobPipeline_BuildAndDeploy()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        buildJob.DisplayName = "Build & Publish";
        var deployJob = workflow.AddJob("deploy");
        deployJob.DisplayName = "Deploy to Azure";

        var buildStep = new PipelineStep
        {
            Name = "build-app",
            Action = _ => Task.CompletedTask,
            ScheduledBy = buildJob
        };

        var deployStep = new PipelineStep
        {
            Name = "deploy-app",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = ["build-app"],
            ScheduledBy = deployJob
        };

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);
        var output = WorkflowYamlSerializer.Serialize(yaml);

        return Verify(output).UseDirectory("Snapshots");
    }

    [Fact]
    public Task ThreeJobDiamond_FanOutAndIn()
    {
        var workflow = new GitHubActionsWorkflowResource("ci-cd");
        var buildJob = workflow.AddJob("build");
        buildJob.DisplayName = "Build";
        var testJob = workflow.AddJob("test");
        testJob.DisplayName = "Run Tests";
        var deployJob = workflow.AddJob("deploy");
        deployJob.DisplayName = "Deploy";

        var buildStep = new PipelineStep
        {
            Name = "build-app",
            Action = _ => Task.CompletedTask,
            ScheduledBy = buildJob
        };

        var testStep = new PipelineStep
        {
            Name = "run-tests",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = ["build-app"],
            ScheduledBy = testJob
        };

        var deployStep = new PipelineStep
        {
            Name = "deploy-app",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = ["build-app", "run-tests"],
            ScheduledBy = deployJob
        };

        var scheduling = SchedulingResolver.Resolve([buildStep, testStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);
        var output = WorkflowYamlSerializer.Serialize(yaml);

        return Verify(output).UseDirectory("Snapshots");
    }

    [Fact]
    public Task CustomRunsOn_WindowsJob()
    {
        var workflow = new GitHubActionsWorkflowResource("build-windows");
        var winJob = workflow.AddJob("build-win");
        winJob.DisplayName = "Build on Windows";
        winJob.RunsOn = "windows-latest";

        var step = new PipelineStep
        {
            Name = "build-app",
            Action = _ => Task.CompletedTask,
            ScheduledBy = winJob
        };

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);
        var output = WorkflowYamlSerializer.Serialize(yaml);

        return Verify(output).UseDirectory("Snapshots");
    }
}
