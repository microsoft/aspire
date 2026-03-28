// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

[Trait("Partition", "4")]
public class SchedulingResolverTests
{
    [Fact]
    public void Resolve_TwoStepsTwoJobs_ValidDependency_CorrectNeeds()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-step", scheduledBy: buildJob);
        var deployStep = CreateStep("deploy-step", deployJob, "build-step");

        var result = SchedulingResolver.Resolve([buildStep, deployStep], workflow);

        Assert.Same(buildJob, result.StepToJob["build-step"]);
        Assert.Same(deployJob, result.StepToJob["deploy-step"]);
        Assert.Contains("build", result.JobDependencies["deploy"]);
    }

    [Fact]
    public void Resolve_FanOut_OneStepDependsOnThreeAcrossThreeJobs()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("job1");
        var job2 = workflow.AddJob("job2");
        var job3 = workflow.AddJob("job3");
        var collectJob = workflow.AddJob("collect");

        var step1 = CreateStep("step1", scheduledBy: job1);
        var step2 = CreateStep("step2", scheduledBy: job2);
        var step3 = CreateStep("step3", scheduledBy: job3);
        var collectStep = CreateStep("collect-step", scheduledBy: collectJob,
            dependsOn: ["step1", "step2", "step3"]);

        var result = SchedulingResolver.Resolve([step1, step2, step3, collectStep], workflow);

        var collectDeps = result.JobDependencies["collect"];
        Assert.Contains("job1", collectDeps);
        Assert.Contains("job2", collectDeps);
        Assert.Contains("job3", collectDeps);
    }

    [Fact]
    public void Resolve_FanIn_ThreeStepsDependOnOne()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var setupJob = workflow.AddJob("setup");
        var job1 = workflow.AddJob("job1");
        var job2 = workflow.AddJob("job2");
        var job3 = workflow.AddJob("job3");

        var setupStep = CreateStep("setup-step", scheduledBy: setupJob);
        var step1 = CreateStep("step1", job1, "setup-step");
        var step2 = CreateStep("step2", job2, "setup-step");
        var step3 = CreateStep("step3", job3, "setup-step");

        var result = SchedulingResolver.Resolve([setupStep, step1, step2, step3], workflow);

        Assert.Contains("setup", result.JobDependencies["job1"]);
        Assert.Contains("setup", result.JobDependencies["job2"]);
        Assert.Contains("setup", result.JobDependencies["job3"]);
    }

    [Fact]
    public void Resolve_Diamond_ValidDagAcrossJobs()
    {
        // A → B, A → C, B → D, C → D
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var jobA = workflow.AddJob("jobA");
        var jobB = workflow.AddJob("jobB");
        var jobC = workflow.AddJob("jobC");
        var jobD = workflow.AddJob("jobD");

        var stepA = CreateStep("A", scheduledBy: jobA);
        var stepB = CreateStep("B", jobB, "A");
        var stepC = CreateStep("C", jobC, "A");
        var stepD = CreateStep("D", jobD, ["B", "C"]);

        var result = SchedulingResolver.Resolve([stepA, stepB, stepC, stepD], workflow);

        Assert.Contains("jobA", result.JobDependencies["jobB"]);
        Assert.Contains("jobA", result.JobDependencies["jobC"]);
        Assert.Contains("jobB", result.JobDependencies["jobD"]);
        Assert.Contains("jobC", result.JobDependencies["jobD"]);
    }

    [Fact]
    public void Resolve_Cycle_ThrowsSchedulingValidationException()
    {
        // Step A on job1 depends on Step B on job2 depends on Step C on job1 depends on Step A
        // This creates job1 → job2 → job1 cycle
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("job1");
        var job2 = workflow.AddJob("job2");

        var stepA = CreateStep("A", job1, "C");
        var stepB = CreateStep("B", job2, "A");
        var stepC = CreateStep("C", job1, "B");

        var ex = Assert.Throws<SchedulingValidationException>(
            () => SchedulingResolver.Resolve([stepA, stepB, stepC], workflow));

        Assert.Contains("circular dependency", ex.Message);
    }

    [Fact]
    public void Resolve_DefaultJob_UnscheduledStepsGrouped()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");

        var step1 = CreateStep("step1"); // No scheduledBy — goes to auto-created default job
        var step2 = CreateStep("step2"); // No scheduledBy — goes to auto-created default job
        var step3 = CreateStep("step3", scheduledBy: buildJob);

        var result = SchedulingResolver.Resolve([step1, step2, step3], workflow);

        // step1 and step2 should be on the auto-created default job, separate from buildJob
        Assert.Same(result.DefaultJob!, result.StepToJob["step1"]);
        Assert.Same(result.DefaultJob!, result.StepToJob["step2"]);
        Assert.Same(buildJob, result.StepToJob["step3"]);
        Assert.NotSame(buildJob, result.DefaultJob!);
        Assert.Equal("default", result.DefaultJob!.Id);
    }

    [Fact]
    public void Resolve_MixedScheduledAndUnscheduled()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var publishJob = workflow.AddJob("publish");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build"); // No scheduledBy → auto-created default job
        var publishStep = CreateStep("publish", publishJob, "build");
        var deployStep = CreateStep("deploy", deployJob, "publish");

        var result = SchedulingResolver.Resolve([buildStep, publishStep, deployStep], workflow);

        // build goes to the auto-created default job
        Assert.Same(result.DefaultJob!, result.StepToJob["build"]);
        Assert.Same(publishJob, result.StepToJob["publish"]);
        Assert.Same(deployJob, result.StepToJob["deploy"]);

        // publish depends on default job (build-step is on default, publish-step depends on build-step)
        Assert.Contains(result.DefaultJob!.Id, result.JobDependencies["publish"]);
        // deploy depends on publish job
        Assert.Contains("publish", result.JobDependencies["deploy"]);
    }

    [Fact]
    public void Resolve_SingleJob_AllStepsOnSameJob_NoJobDependencies()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job = workflow.AddJob("main");

        var step1 = CreateStep("step1", scheduledBy: job);
        var step2 = CreateStep("step2", job, "step1");
        var step3 = CreateStep("step3", job, "step2");

        var result = SchedulingResolver.Resolve([step1, step2, step3], workflow);

        // All on same job, so no cross-job dependencies
        Assert.Empty(result.JobDependencies["main"]);
    }

    [Fact]
    public void Resolve_NoJobs_CreatesDefaultJob()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");

        var step1 = CreateStep("step1");
        var step2 = CreateStep("step2", null, "step1");

        var result = SchedulingResolver.Resolve([step1, step2], workflow);

        Assert.Equal("default", result.DefaultJob!.Id);
        Assert.Same(result.DefaultJob!, result.StepToJob["step1"]);
        Assert.Same(result.DefaultJob!, result.StepToJob["step2"]);
    }

    [Fact]
    public void Resolve_StepsGroupedPerJob_Correctly()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("job1");
        var job2 = workflow.AddJob("job2");

        var stepA = CreateStep("A", scheduledBy: job1);
        var stepB = CreateStep("B", scheduledBy: job1);
        var stepC = CreateStep("C", scheduledBy: job2);

        var result = SchedulingResolver.Resolve([stepA, stepB, stepC], workflow);

        Assert.Equal(2, result.StepsPerJob["job1"].Count);
        Assert.Contains(result.StepsPerJob["job1"], s => s.Name == "A");
        Assert.Contains(result.StepsPerJob["job1"], s => s.Name == "B");
        Assert.Single(result.StepsPerJob["job2"]);
        Assert.Equal("C", result.StepsPerJob["job2"][0].Name);
    }

    [Fact]
    public void Resolve_ExplicitJobDependency_Preserved()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var setupJob = workflow.AddJob("setup");
        var deployJob = workflow.AddJob("deploy");

        // Explicit job-level dependency (not from steps)
        deployJob.DependsOn(setupJob);

        var stepA = CreateStep("A", scheduledBy: setupJob);
        var stepB = CreateStep("B", scheduledBy: deployJob);

        var result = SchedulingResolver.Resolve([stepA, stepB], workflow);

        Assert.Contains("setup", result.JobDependencies["deploy"]);
    }

    [Fact]
    public void Resolve_StepFromDifferentWorkflow_Throws()
    {
        var workflow1 = new GitHubActionsWorkflowResource("deploy");
        var workflow2 = new GitHubActionsWorkflowResource("other");
        var job = workflow2.AddJob("build");

        var step = CreateStep("step1", scheduledBy: job);

        var ex = Assert.Throws<SchedulingValidationException>(
            () => SchedulingResolver.Resolve([step], workflow1));

        Assert.Contains("different workflow", ex.Message);
    }

    [Fact]
    public void Resolve_ExplicitJobDependency_CreatesCycle_Throws()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("job1");
        var job2 = workflow.AddJob("job2");

        // Explicit cycle: job1 → job2 → job1
        job1.DependsOn(job2);
        job2.DependsOn(job1);

        var stepA = CreateStep("A", scheduledBy: job1);
        var stepB = CreateStep("B", scheduledBy: job2);

        var ex = Assert.Throws<SchedulingValidationException>(
            () => SchedulingResolver.Resolve([stepA, stepB], workflow));

        Assert.Contains("circular dependency", ex.Message);
    }

    [Fact]
    public void Resolve_ScheduledByWorkflow_AutoCreatesDefaultJob()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");

        var step = CreateStep("build-step", scheduledBy: workflow);

        var result = SchedulingResolver.Resolve([step], workflow);

        // Should auto-create a default stage + default job
        Assert.Equal("default", result.DefaultJob!.Id);
        Assert.Same(result.DefaultJob!, result.StepToJob["build-step"]);
        Assert.Single(workflow.Stages);
        Assert.Equal("default", workflow.Stages[0].Name);
    }

    [Fact]
    public void Resolve_ScheduledByStage_AutoCreatesDefaultJobOnStage()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildStage = workflow.AddStage("build");

        var step = CreateStep("build-step", scheduledBy: buildStage);

        var result = SchedulingResolver.Resolve([step], workflow);

        // Should auto-create a default job within the build stage (named "build-default")
        var autoJob = result.StepToJob["build-step"];
        Assert.Equal("build-default", autoJob.Id);
        Assert.Single(buildStage.Jobs);
        Assert.Same(autoJob, buildStage.Jobs[0]);
    }

    [Fact]
    public void Resolve_ScheduledByStageAndJob_MixedTargets()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildStage = workflow.AddStage("build");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build-step", scheduledBy: buildStage);
        var deployStep = CreateStep("deploy-step", deployJob, "build-step");

        var result = SchedulingResolver.Resolve([buildStep, deployStep], workflow);

        // build-step should be on the stage's auto-created default job
        Assert.Equal("build-default", result.StepToJob["build-step"].Id);
        Assert.Same(deployJob, result.StepToJob["deploy-step"]);

        // deploy job should depend on build-default job
        Assert.Contains("build-default", result.JobDependencies["deploy"]);
    }

    [Fact]
    public void Resolve_ScheduledByWorkflow_WithExplicitJobs_StillAutoCreates()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var explicitJob = workflow.AddJob("publish");

        // Schedule one step to workflow (auto-default), one to explicit job
        var step1 = CreateStep("setup", scheduledBy: workflow);
        var step2 = CreateStep("publish", explicitJob, "setup");

        var result = SchedulingResolver.Resolve([step1, step2], workflow);

        Assert.Same(result.DefaultJob!, result.StepToJob["setup"]);
        Assert.Same(explicitJob, result.StepToJob["publish"]);
        Assert.NotSame(explicitJob, result.DefaultJob!);
        Assert.Contains(result.DefaultJob!.Id, result.JobDependencies["publish"]);
    }

    [Fact]
    public void Resolve_ScheduledByStageFromDifferentWorkflow_Throws()
    {
        var workflow1 = new GitHubActionsWorkflowResource("deploy");
        var workflow2 = new GitHubActionsWorkflowResource("other");
        var stage = workflow2.AddStage("build");

        var step = CreateStep("step1", scheduledBy: stage);

        var ex = Assert.Throws<SchedulingValidationException>(
            () => SchedulingResolver.Resolve([step], workflow1));

        Assert.Contains("different workflow", ex.Message);
    }

    [Fact]
    public void Resolve_ScheduledByDifferentWorkflow_Throws()
    {
        var workflow1 = new GitHubActionsWorkflowResource("deploy");
        var workflow2 = new GitHubActionsWorkflowResource("other");

        var step = CreateStep("step1", scheduledBy: workflow2);

        var ex = Assert.Throws<SchedulingValidationException>(
            () => SchedulingResolver.Resolve([step], workflow1));

        Assert.Contains("workflow", ex.Message);
    }

    // Helper methods

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
