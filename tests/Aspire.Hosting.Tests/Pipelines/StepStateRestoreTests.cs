// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECOMPUTE001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class StepStateRestoreTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ExecuteAsync_StepWithSuccessfulRestore_SkipsExecution()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var actionExecuted = false;
        pipeline.AddStep(new PipelineStep
        {
            Name = "restorable-step",
            Action = async (_) => { actionExecuted = true; await Task.CompletedTask; },
            TryRestoreStepAsync = _ => Task.FromResult(true)
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.False(actionExecuted, "Step action should not execute when restore succeeds");
    }

    [Fact]
    public async Task ExecuteAsync_StepWithFailedRestore_ExecutesNormally()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var actionExecuted = false;
        pipeline.AddStep(new PipelineStep
        {
            Name = "non-restorable-step",
            Action = async (_) => { actionExecuted = true; await Task.CompletedTask; },
            TryRestoreStepAsync = _ => Task.FromResult(false)
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(actionExecuted, "Step action should execute when restore fails");
    }

    [Fact]
    public async Task ExecuteAsync_StepWithoutRestoreFunc_AlwaysExecutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var actionExecuted = false;
        pipeline.AddStep(new PipelineStep
        {
            Name = "plain-step",
            Action = async (_) => { actionExecuted = true; await Task.CompletedTask; }
            // No TryRestoreStepAsync set
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(actionExecuted, "Step action should execute when no restore func is set");
    }

    [Fact]
    public async Task ExecuteAsync_MixedRestoredAndFresh_CorrectBehavior()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();

        pipeline.AddStep(new PipelineStep
        {
            Name = "step1",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step1"); } await Task.CompletedTask; },
            TryRestoreStepAsync = _ => Task.FromResult(true) // Restorable — will be skipped
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step2",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step2"); } await Task.CompletedTask; },
            DependsOnSteps = ["step1"]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step3",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step3"); } await Task.CompletedTask; }
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.DoesNotContain("step1", executedSteps);
        Assert.Contains("step2", executedSteps);
        Assert.Contains("step3", executedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_RestoredStep_DependentsStillRun()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();

        pipeline.AddStep(new PipelineStep
        {
            Name = "A",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("A"); } await Task.CompletedTask; },
            TryRestoreStepAsync = _ => Task.FromResult(true) // Restored
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "B",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("B"); } await Task.CompletedTask; },
            DependsOnSteps = ["A"]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "C",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("C"); } await Task.CompletedTask; },
            DependsOnSteps = ["B"]
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.DoesNotContain("A", executedSteps);
        Assert.Contains("B", executedSteps);
        Assert.Contains("C", executedSteps);
    }

    private static PipelineContext CreateDeployingContext(DistributedApplication app)
    {
        return new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<StepStateRestoreTests>>(),
            CancellationToken.None);
    }
}
