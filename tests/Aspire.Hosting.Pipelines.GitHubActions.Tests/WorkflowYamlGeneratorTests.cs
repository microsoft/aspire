// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines.GitHubActions.Yaml;

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

[Trait("Partition", "4")]
public class WorkflowYamlGeneratorTests
{
    [Fact]
    public void Generate_BareWorkflow_CreatesDefaultJobWithBoilerplate()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal("deploy", yaml.Name);
        Assert.Single(yaml.Jobs);
        Assert.True(yaml.Jobs.ContainsKey("default"));

        var job = yaml.Jobs["default"];
        Assert.Contains(job.Steps, s => s.Name == "Checkout code");
        Assert.Contains(job.Steps, s => s.Name == "Setup .NET");
        Assert.Contains(job.Steps, s => s.Name == "Install Aspire CLI");
        Assert.Contains(job.Steps, s => s.Run?.Contains("aspire do --continue --job default") == true);
    }

    [Fact]
    public void Generate_TwoJobs_CorrectNeedsDependencies()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal(2, yaml.Jobs.Count);
        Assert.Null(yaml.Jobs["build"].Needs);
        Assert.NotNull(yaml.Jobs["deploy"].Needs);
        Assert.Contains("build", yaml.Jobs["deploy"].Needs!);
    }

    [Fact]
    public void Generate_MultipleJobDeps_NeedsContainsAll()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("build");
        var job2 = workflow.AddJob("test");
        var job3 = workflow.AddJob("deploy");

        var step1 = CreateStep("build-app", job1);
        var step2 = CreateStep("run-tests", job2);
        var step3 = CreateStep("deploy-app", job3, ["build-app", "run-tests"]);

        var scheduling = SchedulingResolver.Resolve([step1, step2, step3], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.NotNull(yaml.Jobs["deploy"].Needs);
        Assert.Contains("build", yaml.Jobs["deploy"].Needs!);
        Assert.Contains("test", yaml.Jobs["deploy"].Needs!);
    }

    [Fact]
    public void Generate_DependentJobs_HasStateDownloadSteps()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        // deploy job should download state from build
        var deployJobYaml = yaml.Jobs["deploy"];
        Assert.Contains(deployJobYaml.Steps, s =>
            s.Name == "Download state from build" &&
            s.Uses == "actions/download-artifact@v4");
    }

    [Fact]
    public void Generate_AllJobs_HaveStateUploadStep()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        foreach (var (_, jobYaml) in yaml.Jobs)
        {
            Assert.Contains(jobYaml.Steps, s =>
                s.Name == "Upload state" &&
                s.Uses == "actions/upload-artifact@v4");
        }
    }

    [Fact]
    public void Generate_JobRunsOn_MatchesJobConfiguration()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        buildJob.RunsOn = "windows-latest";

        var step = CreateStep("build-app", buildJob);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal("windows-latest", yaml.Jobs["build"].RunsOn);
    }

    [Fact]
    public void Generate_JobDisplayName_IsPreserved()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        buildJob.DisplayName = "Build Application";

        var step = CreateStep("build-app", buildJob);

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.Equal("Build Application", yaml.Jobs["build"].Name);
    }

    [Fact]
    public void Generate_DefaultTriggers_WorkflowDispatchAndPush()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var step = CreateStep("build-app");

        var scheduling = SchedulingResolver.Resolve([step], workflow);
        var yaml = WorkflowYamlGenerator.Generate(scheduling, workflow);

        Assert.True(yaml.On.WorkflowDispatch);
        Assert.NotNull(yaml.On.Push);
        Assert.Contains("main", yaml.On.Push!.Branches);
    }

    [Fact]
    public void SerializeRoundTrip_ProducesValidYaml()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");
        buildJob.DisplayName = "Build & Publish";

        var buildStep = CreateStep("build-app", buildJob);
        var deployStep = CreateStep("deploy-app", deployJob, "build-app");

        var scheduling = SchedulingResolver.Resolve([buildStep, deployStep], workflow);
        var yamlModel = WorkflowYamlGenerator.Generate(scheduling, workflow);
        var yamlString = WorkflowYamlSerializer.Serialize(yamlModel);

        // Verify key structural elements
        Assert.Contains("name: deploy", yamlString);
        Assert.Contains("workflow_dispatch:", yamlString);
        Assert.Contains("push:", yamlString);
        Assert.Contains("branches:", yamlString);
        Assert.Contains("- main", yamlString);
        Assert.Contains("  build:", yamlString);
        Assert.Contains("  deploy:", yamlString);
        Assert.Contains("needs:", yamlString);
        Assert.Contains("actions/checkout@v4", yamlString);
        Assert.Contains("actions/setup-dotnet@v4", yamlString);
        Assert.Contains("aspire do --continue --job build", yamlString);
        Assert.Contains("aspire do --continue --job deploy", yamlString);
        Assert.Contains("actions/upload-artifact@v4", yamlString);
        Assert.Contains("actions/download-artifact@v4", yamlString);
        Assert.Contains("'Build & Publish'", yamlString); // Quoted because of &
    }

    // Helpers

    private static PipelineStep CreateStep(string name, IPipelineStepTarget? scheduledBy = null)
    {
        return new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            ScheduledBy = scheduledBy
        };
    }

    private static PipelineStep CreateStep(string name, IPipelineStepTarget? scheduledBy, string dependsOn)
    {
        return new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [dependsOn],
            ScheduledBy = scheduledBy
        };
    }

    private static PipelineStep CreateStep(string name, IPipelineStepTarget? scheduledBy, string[] dependsOn)
    {
        return new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [.. dependsOn],
            ScheduledBy = scheduledBy
        };
    }
}
