// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.Tests.Publishing;

public class PipelineExecutorTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ListStepsDoesNotRunLifecycleCallbacksOrExecutePipeline()
    {
        var beforeStartCalled = false;
        var lifecycleHookCalled = false;
        var beforePublishCalled = false;
        var pipelineStepCalled = false;

        using var builder = TestDistributedApplicationBuilder
            .Create(["--operation", "inspect", "--list-steps", "true"])
            .WithTestAndResourceLogging(testOutputHelper);
        builder.OnBeforeStart((_, _) =>
        {
            beforeStartCalled = true;
            return Task.CompletedTask;
        });
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but inspection must not invoke existing hooks.
        builder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(new CallbackLifecycleHook(() => lifecycleHookCalled = true));
#pragma warning restore CS0618
        builder.OnBeforePublish((_, _) =>
        {
            beforePublishCalled = true;
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        pipeline.AddStep("must-not-run", _ =>
        {
            pipelineStepCalled = true;
            return Task.CompletedTask;
        });

        await app.StartAsync().DefaultTimeout();

        Assert.False(beforeStartCalled);
        Assert.False(lifecycleHookCalled);
        Assert.False(beforePublishCalled);
        Assert.False(pipelineStepCalled);

        await app.StopAsync().DefaultTimeout();
    }

#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but inspection must not invoke existing hooks.
    private sealed class CallbackLifecycleHook(Action callback) : IDistributedApplicationLifecycleHook
#pragma warning restore CS0618
    {
        public Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            callback();
            return Task.CompletedTask;
        }
    }
}