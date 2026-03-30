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
    public void Resolve_DefaultJob_UnscheduledStepsPulledToFirstJob()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var buildJob = workflow.AddJob("build");

        var step1 = CreateStep("step1"); // No scheduledBy — pulled to first available job
        var step2 = CreateStep("step2"); // No scheduledBy — pulled to first available job
        var step3 = CreateStep("step3", scheduledBy: buildJob);

        var result = SchedulingResolver.Resolve([step1, step2, step3], workflow);

        // Orphan unscheduled steps (no consumer) go to the first available job
        Assert.Same(buildJob, result.StepToJob["step1"]);
        Assert.Same(buildJob, result.StepToJob["step2"]);
        Assert.Same(buildJob, result.StepToJob["step3"]);
    }

    [Fact]
    public void Resolve_MixedScheduledAndUnscheduled_PullsToConsumer()
    {
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var publishJob = workflow.AddJob("publish");
        var deployJob = workflow.AddJob("deploy");

        var buildStep = CreateStep("build"); // No scheduledBy → pulled to first consumer (publish)
        var publishStep = CreateStep("publish", publishJob, "build");
        var deployStep = CreateStep("deploy", deployJob, "publish");

        var result = SchedulingResolver.Resolve([buildStep, publishStep, deployStep], workflow);

        // build is pulled into publishJob because publish is the first thing that needs it
        Assert.Same(publishJob, result.StepToJob["build"]);
        Assert.Same(publishJob, result.StepToJob["publish"]);
        Assert.Same(deployJob, result.StepToJob["deploy"]);

        // No cross-job dependency for build→publish (same job)
        // deploy depends on publish job
        Assert.Contains("publish", result.JobDependencies["deploy"]);
        Assert.DoesNotContain("default", result.JobDependencies.Keys);
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

    [Fact]
    public void Resolve_UnscheduledChain_PulledToFirstExplicitConsumer()
    {
        // A → B → C, where C is explicitly scheduled. A and B should be pulled into C's job.
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var publishJob = workflow.AddJob("publish");

        var stepA = CreateStep("A");
        var stepB = CreateStep("B", null, "A");
        var stepC = CreateStep("C", publishJob, "B");

        var result = SchedulingResolver.Resolve([stepA, stepB, stepC], workflow);

        Assert.Same(publishJob, result.StepToJob["A"]);
        Assert.Same(publishJob, result.StepToJob["B"]);
        Assert.Same(publishJob, result.StepToJob["C"]);
        Assert.Empty(result.JobDependencies["publish"]);
    }

    [Fact]
    public void Resolve_ExplicitStages_NoDefaultStageCreated()
    {
        // Two stages: publish and deploy. Unscheduled "build" depends on nothing,
        // but publish depends on build → build is pulled into publish stage.
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var publishStage = workflow.AddStage("publish");
        var deployStage = workflow.AddStage("deploy");

        var buildStep = CreateStep("build");
        var publishStep = CreateStep("publish", publishStage, "build");
        var deployStep = CreateStep("deploy", deployStage, "publish");

        var result = SchedulingResolver.Resolve([buildStep, publishStep, deployStep], workflow);

        // build is pulled into publish stage's default job
        Assert.Equal("publish-default", result.StepToJob["build"].Id);
        Assert.Equal("publish-default", result.StepToJob["publish"].Id);
        Assert.Equal("deploy-default", result.StepToJob["deploy"].Id);

        // No default stage should have been created
        Assert.DoesNotContain(workflow.Stages, s => s.Name == "default");

        // deploy-default job should depend on publish-default job
        Assert.True(result.JobDependencies.TryGetValue("deploy-default", out var deployDeps), "deploy-default should have job dependencies");
        Assert.Contains("publish-default", deployDeps);
    }

    [Fact]
    public void Resolve_FanOut_UnscheduledPulledToFirstConsumer()
    {
        // A is unscheduled, both B (job1) and C (job2) depend on A.
        // A is pulled to the first consumer found (job1).
        var workflow = new GitHubActionsWorkflowResource("deploy");
        var job1 = workflow.AddJob("job1");
        var job2 = workflow.AddJob("job2");

        var stepA = CreateStep("A");
        var stepB = CreateStep("B", job1, "A");
        var stepC = CreateStep("C", job2, "A");

        var result = SchedulingResolver.Resolve([stepA, stepB, stepC], workflow);

        // A pulled to the first consumer's job (job1)
        Assert.Same(job1, result.StepToJob["A"]);
    }

    [Fact]
    public void Resolve_RequiredByWithTwoStages_NoCycleAfterNormalization()
    {
        // Simulates the docker-compose scenario:
        //   publish-env RequiredBy "publish" (scheduled to stage1)
        //   prepare-env DependsOn "publish", DependsOn "build"
        //   docker-compose-up-env DependsOn "prepare-env", RequiredBy "deploy" (scheduled to stage2)
        //
        // Before normalization, the RequiredBy edges are invisible to the scheduler,
        // causing incorrect job assignments. After normalization, the scheduler should
        // correctly see that "publish" depends on "publish-env" and "deploy" depends
        // on "docker-compose-up-env".
        var workflow = new GitHubActionsWorkflowResource("ci");
        var stage1 = workflow.AddStage("build");
        var stage2 = workflow.AddStage("deploy");

        var publishEnv = CreateStepWithRequiredBy("publish-env", "publish");
        var publish = CreateStep("publish", stage1);
        var build = CreateStep("build", stage1);
        var prepareEnv = CreateStep("prepare-env", scheduledBy: null, dependsOn: ["publish", "build"]);
        var dockerComposeUp = CreateStepWithRequiredBy("docker-compose-up-env", "deploy");
        dockerComposeUp.DependsOnSteps.Add("prepare-env");
        var deploy = CreateStep("deploy", stage2);

        var steps = new List<PipelineStep> { publishEnv, publish, build, prepareEnv, dockerComposeUp, deploy };

        // Normalize RequiredBy→DependsOn (same as GitHubActionsWorkflowExtensions does)
        var stepsByName = steps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var requiredByStepName in step.RequiredBySteps)
            {
                if (stepsByName.TryGetValue(requiredByStepName, out var requiredByStep) &&
                    !requiredByStep.DependsOnSteps.Contains(step.Name))
                {
                    requiredByStep.DependsOnSteps.Add(step.Name);
                }
            }
        }

        // After normalization: publish depends on publish-env, deploy depends on docker-compose-up-env
        Assert.Contains("publish-env", publish.DependsOnSteps);
        Assert.Contains("docker-compose-up-env", deploy.DependsOnSteps);

        // Should not throw SchedulingValidationException (no cycle)
        var result = SchedulingResolver.Resolve(steps, workflow);

        // publish and build are in stage1 (build-default job)
        Assert.Equal("build-default", result.StepToJob["publish"].Id);
        Assert.Equal("build-default", result.StepToJob["build"].Id);
        Assert.Equal("build-default", result.StepToJob["publish-env"].Id);

        // deploy is in stage2 (deploy-default job)
        Assert.Equal("deploy-default", result.StepToJob["deploy"].Id);

        // deploy-default should depend on build-default (not the other way around)
        Assert.True(result.JobDependencies.TryGetValue("deploy-default", out var deployDeps));
        Assert.Contains("build-default", deployDeps);
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

    private static PipelineStep CreateStepWithRequiredBy(string name, string requiredBy, IPipelineStepTarget? scheduledBy = null)
    {
        var step = new PipelineStep
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            ScheduledBy = scheduledBy
        };
        step.RequiredBy(requiredBy);
        return step;
    }
}
