// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

[Trait("Partition", "4")]
public class GitHubActionsWorkflowResourceTests
{
    [Fact]
    public void WorkflowFileName_MatchesResourceName()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");

        Assert.Equal("deploy.yml", workflow.WorkflowFileName);
    }

    [Fact]
    public void AddJob_CreatesJobWithCorrectId()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job = workflow.AddJob("build");

        Assert.Equal("build", job.Id);
        Assert.Same(workflow, job.Workflow);
    }

    [Fact]
    public void AddJob_MultipleJobs_AllTracked()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var build = workflow.AddJob("build");
        var test = workflow.AddJob("test");
        var deploy = workflow.AddJob("deploy");

        Assert.Equal(3, workflow.Jobs.Count);
        Assert.Same(build, workflow.Jobs[0]);
        Assert.Same(test, workflow.Jobs[1]);
        Assert.Same(deploy, workflow.Jobs[2]);
    }

    [Fact]
    public void AddJob_DuplicateId_Throws()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        workflow.AddJob("build");

        var ex = Assert.Throws<InvalidOperationException>(() => workflow.AddJob("build"));
        Assert.Contains("build", ex.Message);
    }

    [Fact]
    public void Job_DependsOn_ById()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        workflow.AddJob("build");
        var deploy = workflow.AddJob("deploy");

        deploy.DependsOn("build");

        Assert.Single(deploy.DependsOnJobs);
        Assert.Equal("build", deploy.DependsOnJobs[0]);
    }

    [Fact]
    public void Job_DependsOn_ByReference()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var build = workflow.AddJob("build");
        var deploy = workflow.AddJob("deploy");

        deploy.DependsOn(build);

        Assert.Single(deploy.DependsOnJobs);
        Assert.Equal("build", deploy.DependsOnJobs[0]);
    }

    [Fact]
    public void Job_DefaultRunsOn_IsUbuntuLatest()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job = workflow.AddJob("build");

        Assert.Equal("ubuntu-latest", job.RunsOn);
    }

    [Fact]
    public void Job_IPipelineStepTarget_EnvironmentIsWorkflow()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job = workflow.AddJob("build");

        IPipelineStepTarget target = job;

        Assert.Same(workflow, target.Environment);
    }

    [Fact]
    public void Workflow_ImplementsIPipelineEnvironment()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");

        Assert.IsAssignableFrom<IPipelineEnvironment>(workflow);
    }

    [Fact]
    public void AddStage_CreatesStageWithCorrectName()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");

        var stage = workflow.AddStage("build-stage");

        Assert.Equal("build-stage", stage.Name);
        Assert.Same(workflow, stage.Workflow);
        Assert.Single(workflow.Stages);
    }

    [Fact]
    public void AddStage_DuplicateName_Throws()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        workflow.AddStage("build-stage");

        Assert.Throws<InvalidOperationException>(() => workflow.AddStage("build-stage"));
    }

    [Fact]
    public void Stage_AddJob_CreatesJobOnWorkflow()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var stage = workflow.AddStage("build-stage");

        var job = stage.AddJob("build");

        Assert.Equal("build", job.Id);
        Assert.Single(stage.Jobs);
        Assert.Single(workflow.Jobs); // Job is also registered on the workflow
        Assert.Same(job, workflow.Jobs[0]);
    }

    [Fact]
    public void Stage_AddJob_MultipleStagesWithJobs()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildStage = workflow.AddStage("build-stage");
        var deployStage = workflow.AddStage("deploy-stage");

        var buildJob = buildStage.AddJob("build");
        var deployJob = deployStage.AddJob("deploy");

        Assert.Single(buildStage.Jobs);
        Assert.Single(deployStage.Jobs);
        Assert.Equal(2, workflow.Jobs.Count);
        Assert.Same(buildJob, buildStage.Jobs[0]);
        Assert.Same(deployJob, deployStage.Jobs[0]);
    }

    [Fact]
    public void JobResource_ExtendsResource()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job = workflow.AddJob("build");

        Assert.IsAssignableFrom<Aspire.Hosting.ApplicationModel.Resource>(job);
    }

    [Fact]
    public void StageResource_ExtendsResource()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var stage = workflow.AddStage("build-stage");

        Assert.IsAssignableFrom<Aspire.Hosting.ApplicationModel.Resource>(stage);
    }
}
