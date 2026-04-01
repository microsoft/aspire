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
public class ScopeFilteringTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ContinuationMode_InScopeStepsExecute_OutOfScopeSkipped()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, step: null)
            .WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        // Add a fake pipeline environment with scope annotation
        var envResource = new FakePipelineEnvironment("test-env");
        envResource.Annotations.Add(new PipelineEnvironmentCheckAnnotation(
            _ => Task.FromResult(true)));
        envResource.Annotations.Add(new PipelineScopeAnnotation(
            _ => Task.FromResult<PipelineScopeResult?>(new PipelineScopeResult
            {
                RunId = "run-1",
                JobId = "deploy"
            })));
        envResource.Annotations.Add(new PipelineScopeMapAnnotation(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["build"] = ["step-build"],
                ["deploy"] = ["step-deploy"]
            }));
        builder.AddResource(envResource);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var pipeline = new DistributedApplicationPipeline(model);

        var executedSteps = new List<string>();

        pipeline.AddStep(new PipelineStep
        {
            Name = "step-build",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step-build"); } await Task.CompletedTask; }
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step-deploy",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step-deploy"); } await Task.CompletedTask; },
            DependsOnSteps = ["step-build"]
        });

        var context = CreateContext(app);
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // step-build is out-of-scope (belongs to "build" job), should be skipped
        Assert.DoesNotContain("step-build", executedSteps);
        // step-deploy is in-scope, should execute
        Assert.Contains("step-deploy", executedSteps);
    }

    [Fact]
    public async Task ContinuationMode_OutOfScopeWithRestore_RestoresNotExecutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, step: null)
            .WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var envResource = new FakePipelineEnvironment("test-env");
        envResource.Annotations.Add(new PipelineEnvironmentCheckAnnotation(
            _ => Task.FromResult(true)));
        envResource.Annotations.Add(new PipelineScopeAnnotation(
            _ => Task.FromResult<PipelineScopeResult?>(new PipelineScopeResult
            {
                RunId = "run-1",
                JobId = "deploy"
            })));
        envResource.Annotations.Add(new PipelineScopeMapAnnotation(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["build"] = ["step-build"],
                ["deploy"] = ["step-deploy"]
            }));
        builder.AddResource(envResource);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var pipeline = new DistributedApplicationPipeline(model);

        var restoreCalled = false;
        var actionCalled = false;

        pipeline.AddStep(new PipelineStep
        {
            Name = "step-build",
            Action = async (_) => { actionCalled = true; await Task.CompletedTask; },
            TryRestoreStepAsync = _ => { restoreCalled = true; return Task.FromResult(true); }
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step-deploy",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = ["step-build"]
        });

        var context = CreateContext(app);
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Restore should be called for the out-of-scope step
        Assert.True(restoreCalled, "TryRestoreStepAsync should be called for out-of-scope steps");
        // But the action should not
        Assert.False(actionCalled, "Action should not execute for out-of-scope steps");
    }

    [Fact]
    public async Task NoScope_AllStepsExecute()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, step: null)
            .WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var app = builder.Build();
        // Parameterless constructor: no model → no environment → no scope → all steps execute
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();

        pipeline.AddStep(new PipelineStep
        {
            Name = "step-build",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step-build"); } await Task.CompletedTask; }
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step-deploy",
            Action = async (_) => { lock (executedSteps) { executedSteps.Add("step-deploy"); } await Task.CompletedTask; },
            DependsOnSteps = ["step-build"]
        });

        var context = CreateContext(app);
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // When no scope is detected, all steps execute normally
        Assert.Contains("step-build", executedSteps);
        Assert.Contains("step-deploy", executedSteps);
    }

    private static PipelineContext CreateContext(DistributedApplication app)
    {
        return new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<ScopeFilteringTests>>(),
            CancellationToken.None);
    }

    private sealed class FakePipelineEnvironment(string name) : Resource(name), IPipelineEnvironment
    {
    }
}
