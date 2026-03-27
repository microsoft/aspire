// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class ScheduleStepTests
{
    [Fact]
    public void ScheduleStep_ExistingStep_SetsScheduledBy()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);
        var target = new TestStepTarget("build-job");

        pipeline.AddStep("build-app", _ => Task.CompletedTask);
        pipeline.ScheduleStep("build-app", target);

        var steps = GetSteps(pipeline);
        var step = steps.Single(s => s.Name == "build-app");
        Assert.Same(target, step.ScheduledBy);
    }

    [Fact]
    public void ScheduleStep_NonExistentStep_Throws()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);
        var target = new TestStepTarget("build-job");

        var ex = Assert.Throws<InvalidOperationException>(
            () => pipeline.ScheduleStep("does-not-exist", target));
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void ScheduleStep_MultipleStepsOnDifferentTargets_AllScheduled()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);
        var buildTarget = new TestStepTarget("build-job");
        var deployTarget = new TestStepTarget("deploy-job");

        pipeline.AddStep("build-app", _ => Task.CompletedTask);
        pipeline.AddStep("deploy-app", _ => Task.CompletedTask, dependsOn: "build-app");

        pipeline.ScheduleStep("build-app", buildTarget);
        pipeline.ScheduleStep("deploy-app", deployTarget);

        var steps = GetSteps(pipeline);
        Assert.Same(buildTarget, steps.Single(s => s.Name == "build-app").ScheduledBy);
        Assert.Same(deployTarget, steps.Single(s => s.Name == "deploy-app").ScheduledBy);
    }

    [Fact]
    public void ScheduleStep_OverridesPreviousScheduling()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);
        var target1 = new TestStepTarget("job1");
        var target2 = new TestStepTarget("job2");

        pipeline.AddStep("my-step", _ => Task.CompletedTask, scheduledBy: target1);
        pipeline.ScheduleStep("my-step", target2);

        var steps = GetSteps(pipeline);
        Assert.Same(target2, steps.Single(s => s.Name == "my-step").ScheduledBy);
    }

    [Fact]
    public void ScheduleStep_NullStepName_ThrowsArgumentNull()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);
        var target = new TestStepTarget("build-job");

        Assert.Throws<ArgumentNullException>(() => pipeline.ScheduleStep(null!, target));
    }

    [Fact]
    public void ScheduleStep_NullTarget_ThrowsArgumentNull()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);

        pipeline.AddStep("my-step", _ => Task.CompletedTask);

        Assert.Throws<ArgumentNullException>(() => pipeline.ScheduleStep("my-step", null!));
    }

    [Fact]
    public void ScheduleStep_BuiltInSteps_CanBeScheduled()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);
        var buildTarget = new TestStepTarget("build-job");
        var deployTarget = new TestStepTarget("deploy-job");

        // Built-in steps already exist from constructor
        pipeline.ScheduleStep(WellKnownPipelineSteps.Build, buildTarget);
        pipeline.ScheduleStep(WellKnownPipelineSteps.Deploy, deployTarget);

        var steps = GetSteps(pipeline);
        Assert.Same(buildTarget, steps.Single(s => s.Name == WellKnownPipelineSteps.Build).ScheduledBy);
        Assert.Same(deployTarget, steps.Single(s => s.Name == WellKnownPipelineSteps.Deploy).ScheduledBy);
    }

    private static List<PipelineStep> GetSteps(DistributedApplicationPipeline pipeline)
    {
        var field = typeof(DistributedApplicationPipeline)
            .GetField("_steps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (List<PipelineStep>)field.GetValue(pipeline)!;
    }

    private sealed class TestStepTarget(string id) : IPipelineStepTarget
    {
        public string Id => id;
        public IPipelineEnvironment Environment { get; } = new StubPipelineEnvironment("test-env");
    }

    private sealed class StubPipelineEnvironment(string name) : Resource(name), IPipelineEnvironment;
}
