// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECOMPUTE001
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable IDE0005

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Pipelines;

public class DistributedApplicationPipelineTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ExecuteAsync_WithNoSteps_CompletesSuccessfully()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());

        await pipeline.ExecuteAsync(context).DefaultTimeout();
    }

    [Fact]
    public async Task ExecuteAsync_WithSingleStep_ExecutesStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var stepExecuted = false;
        pipeline.AddStep("step1", async (context) =>
        {
            stepExecuted = true;
            await Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(stepExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleIndependentSteps_ExecutesAllSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();
        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step2"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(3, executedSteps.Count);
        Assert.Contains("step1", executedSteps);
        Assert.Contains("step2", executedSteps);
        Assert.Contains("step3", executedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_WithDependsOn_ExecutesInOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();
        pipeline.AddStep("step1", async (context) =>
        {
            executedSteps.Add("step1");
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            executedSteps.Add("step2");
            await Task.CompletedTask;
        }, dependsOn: "step1");

        pipeline.AddStep("step3", async (context) =>
        {
            executedSteps.Add("step3");
            await Task.CompletedTask;
        }, dependsOn: "step2");

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(["step1", "step2", "step3"], executedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_WithRequiredBy_ExecutesInCorrectOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();
        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        }, requiredBy: "step2");

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step2"); }
            await Task.CompletedTask;
        }, requiredBy: "step3");

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(["step1", "step2", "step3"], executedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_WithMixedDependsOnAndRequiredBy_ExecutesInCorrectOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();
        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step2"); }
            await Task.CompletedTask;
        }, requiredBy: "step3");

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        }, dependsOn: "step1");

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(3, executedSteps.Count);
        var step1Index = executedSteps.IndexOf("step1");
        var step2Index = executedSteps.IndexOf("step2");
        var step3Index = executedSteps.IndexOf("step3");

        Assert.True(step1Index < step3Index, "step1 should execute before step3");
        Assert.True(step2Index < step3Index, "step2 should execute before step3");
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleLevels_ExecutesLevelsInOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executionOrder = new List<(string step, DateTime time)>();
        var executionOrderLock = new object();

        pipeline.AddStep("level1-step1", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add(("level1-step1", DateTime.UtcNow)); }
            await Task.Delay(10);
        });

        pipeline.AddStep("level1-step2", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add(("level1-step2", DateTime.UtcNow)); }
            await Task.Delay(10);
        });

        pipeline.AddStep("level2-step1", (context) =>
        {
            lock (executionOrder) { executionOrder.Add(("level2-step1", DateTime.UtcNow)); }
            return Task.CompletedTask;
        }, dependsOn: "level1-step1");

        pipeline.AddStep("level2-step2", (context) =>
        {
            lock (executionOrder) { executionOrder.Add(("level2-step2", DateTime.UtcNow)); }
            return Task.CompletedTask;
        }, dependsOn: "level1-step2");

        pipeline.AddStep("level3-step1", (context) =>
        {
            lock (executionOrder) { executionOrder.Add(("level3-step1", DateTime.UtcNow)); }
            return Task.CompletedTask;
        }, dependsOn: "level2-step1");

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(5, executionOrder.Count);

        // With readiness-based scheduling, we only guarantee that dependencies are respected,
        // not that all steps at a given "level" complete before the next "level" starts.
        // Verify that each step starts after its direct dependencies.
        var stepTimes = executionOrder.ToDictionary(x => x.step, x => x.time);

        Assert.True(stepTimes["level2-step1"] >= stepTimes["level1-step1"],
            "level2-step1 should start after level1-step1");
        Assert.True(stepTimes["level2-step2"] >= stepTimes["level1-step2"],
            "level2-step2 should start after level1-step2");
        Assert.True(stepTimes["level3-step1"] >= stepTimes["level2-step1"],
            "level3-step1 should start after level2-step1");
    }

    [Fact]
    public async Task ExecuteAsync_WithPipelineStepFactoryAnnotation_ExecutesAnnotatedSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory((factoryContext) => new PipelineStep
            {
                Name = "annotated-step",
                Action = async (ctx) =>
                {
                    lock (executedSteps) { executedSteps.Add("annotated-step"); }
                    await Task.CompletedTask;
                }
            });

        var pipeline = new DistributedApplicationPipeline();
        pipeline.AddStep("regular-step", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("regular-step"); }
            await Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(2, executedSteps.Count);
        Assert.Contains("annotated-step", executedSteps);
        Assert.Contains("regular-step", executedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultiplePipelineStepAnnotations_ExecutesAllAnnotatedSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory((factoryContext) =>
            [
                new PipelineStep
                {
                    Name = "annotated-step-1",
                    Action = async (ctx) =>
                    {
                        lock (executedSteps) { executedSteps.Add("annotated-step-1"); }
                        await Task.CompletedTask;
                    }
                },
                new PipelineStep
                {
                    Name = "annotated-step-2",
                    Action = async (ctx) =>
                    {
                        lock (executedSteps) { executedSteps.Add("annotated-step-2"); }
                        await Task.CompletedTask;
                    }
                }
            ]);

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(2, executedSteps.Count);
        Assert.Contains("annotated-step-1", executedSteps);
        Assert.Contains("annotated-step-2", executedSteps);
    }

    [Fact]
    public void AddStep_WithDuplicateStepNames_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) => await Task.CompletedTask);

        var ex = Assert.Throws<InvalidOperationException>(() => pipeline.AddStep("step1", async (context) => await Task.CompletedTask));
        Assert.Contains("A step with the name 'step1' has already been added", ex.Message);
        Assert.Contains("step1", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownDependency_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) => await Task.CompletedTask, dependsOn: "unknown-step");

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("depends on unknown step", ex.Message);
        Assert.Contains("unknown-step", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownRequiredBy_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) => await Task.CompletedTask, requiredBy: "unknown-step");

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("required by unknown step", ex.Message);
        Assert.Contains("unknown-step", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithCircularDependency_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var step1 = new PipelineStep
        {
            Name = "step1",
            Action = async (context) => await Task.CompletedTask
        };
        step1.DependsOn("step2");

        var step2 = new PipelineStep
        {
            Name = "step2",
            Action = async (context) => await Task.CompletedTask
        };
        step2.DependsOn("step1");

        pipeline.AddStep(step1);
        pipeline.AddStep(step2);

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("Circular dependency", ex.Message);
        Assert.Contains("step1", ex.Message);
        Assert.Contains("step2", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStepThrows_WrapsExceptionWithStepName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var exceptionMessage = "Test exception";
        pipeline.AddStep("failing-step", async (context) =>
        {
            await Task.CompletedTask;
            throw new NotSupportedException(exceptionMessage);
        });

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("failing-step", ex.Message);
        Assert.Contains("failed", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(exceptionMessage, ex.InnerException.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithComplexDependencyGraph_ExecutesInCorrectOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();

        pipeline.AddStep("a", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("a"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("b", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("b"); }
            await Task.CompletedTask;
        }, dependsOn: "a");

        pipeline.AddStep("c", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("c"); }
            await Task.CompletedTask;
        }, dependsOn: "a");

        pipeline.AddStep("d", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("d"); }
            await Task.CompletedTask;
        }, dependsOn: "b", requiredBy: "e");

        pipeline.AddStep("e", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("e"); }
            await Task.CompletedTask;
        }, dependsOn: "c");

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(5, executedSteps.Count);

        var aIndex = executedSteps.IndexOf("a");
        var bIndex = executedSteps.IndexOf("b");
        var cIndex = executedSteps.IndexOf("c");
        var dIndex = executedSteps.IndexOf("d");
        var eIndex = executedSteps.IndexOf("e");

        Assert.True(aIndex < bIndex, "a should execute before b");
        Assert.True(aIndex < cIndex, "a should execute before c");
        Assert.True(bIndex < dIndex, "b should execute before d");
        Assert.True(cIndex < eIndex, "c should execute before e");
        Assert.True(dIndex < eIndex, "d should execute before e (requiredBy relationship)");
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleDependencies_ExecutesInCorrectOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();
        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step2"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        }, dependsOn: new[] { "step1", "step2" });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        var step1Index = executedSteps.IndexOf("step1");
        var step2Index = executedSteps.IndexOf("step2");
        var step3Index = executedSteps.IndexOf("step3");

        Assert.True(step1Index < step3Index, "step1 should execute before step3");
        Assert.True(step2Index < step3Index, "step2 should execute before step3");
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleRequiredBy_ExecutesInCorrectOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();
        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        }, requiredBy: new[] { "step2", "step3" });

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step2"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        var step1Index = executedSteps.IndexOf("step1");
        var step2Index = executedSteps.IndexOf("step2");
        var step3Index = executedSteps.IndexOf("step3");

        Assert.True(step1Index < step2Index, "step1 should execute before step2");
        Assert.True(step1Index < step3Index, "step1 should execute before step3");
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownRequiredByStep_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) =>
        {
            await Task.CompletedTask;
        }, requiredBy: "unknown-step");

        var context = CreateDeployingContext(builder.Build());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("Step 'step1' is required by unknown step 'unknown-step'", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownRequiredByStepInList_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) =>
        {
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            await Task.CompletedTask;
        }, requiredBy: new[] { "step1", "unknown-step" });

        var context = CreateDeployingContext(builder.Build());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("Step 'step2' is required by unknown step 'unknown-step'", exception.Message);
    }

    [Fact]
    public void AddStep_WithInvalidDependsOnType_ThrowsArgumentException()
    {
        var pipeline = new DistributedApplicationPipeline();

        var exception = Assert.Throws<ArgumentException>(() =>
            pipeline.AddStep("step1", async (context) => await Task.CompletedTask, dependsOn: 123));

        Assert.Contains("The dependsOn parameter must be a string or IEnumerable<string>", exception.Message);
    }

    [Fact]
    public void AddStep_WithInvalidRequiredByType_ThrowsArgumentException()
    {
        var pipeline = new DistributedApplicationPipeline();

        var exception = Assert.Throws<ArgumentException>(() =>
            pipeline.AddStep("step1", async (context) => await Task.CompletedTask, requiredBy: 123));

        Assert.Contains("The requiredBy parameter must be a string or IEnumerable<string>", exception.Message);
    }

    [Fact]
    public void AddStep_WithDuplicateName_ThrowsInvalidOperationException()
    {
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) => await Task.CompletedTask);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            pipeline.AddStep("step1", async (context) => await Task.CompletedTask));

        Assert.Contains("A step with the name 'step1' has already been added to the pipeline", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithDuplicateAnnotationStepNames_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var resource1 = builder.AddResource(new CustomResource("resource1"))
            .WithPipelineStepFactory((factoryContext) => new PipelineStep
            {
                Name = "duplicate-step",
                Action = async (ctx) => await Task.CompletedTask
            });

        var resource2 = builder.AddResource(new CustomResource("resource2"))
            .WithPipelineStepFactory((factoryContext) => new PipelineStep
            {
                Name = "duplicate-step",
                Action = async (ctx) => await Task.CompletedTask
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("Duplicate step name", exception.Message);
        Assert.Contains("duplicate-step", exception.Message);
    }

    // Test for multiple failing steps at the same level removed due to inherent race conditions.
    // See https://github.com/dotnet/aspire/issues/12200

    [Fact]
    public async Task ExecuteAsync_WithFailingStep_PreservesOriginalStackTrace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("failing-step", async (context) =>
        {
            await Task.CompletedTask;
            ThrowHelperMethod();
        });

        var context = CreateDeployingContext(builder.Build());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("failing-step", exception.Message);
        Assert.NotNull(exception.InnerException);
        Assert.Contains("ThrowHelperMethod", exception.InnerException.StackTrace);
    }

    private static void ThrowHelperMethod()
    {
        throw new NotSupportedException("Test exception for stack trace");
    }

    [Fact]
    public async Task ExecuteAsync_WithDependencyFailure_ReportsFailedDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var dependentStepExecuted = false;

        // Step that will fail
        pipeline.AddStep("failing-dependency", async (context) =>
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("Dependency failed");
        });

        // Step that depends on the failing step
        pipeline.AddStep("dependent-step", async (context) =>
        {
            dependentStepExecuted = true;
            await Task.CompletedTask;
        }, dependsOn: "failing-dependency");

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();

        // The dependent step should not have executed
        Assert.False(dependentStepExecuted, "Dependent step should not execute when dependency fails");

        // The error message should indicate which dependency failed
        Assert.Contains("failing-dependency", ex.Message);
        Assert.Contains("failed", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithCircularDependencyInComplex_ThrowsInvalidOperationException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        // Create a more complex circular dependency: A -> B -> C -> A
        var stepA = new PipelineStep
        {
            Name = "stepA",
            Action = async (context) => await Task.CompletedTask
        };
        stepA.DependsOn("stepC");

        var stepB = new PipelineStep
        {
            Name = "stepB",
            Action = async (context) => await Task.CompletedTask
        };
        stepB.DependsOn("stepA");

        var stepC = new PipelineStep
        {
            Name = "stepC",
            Action = async (context) => await Task.CompletedTask
        };
        stepC.DependsOn("stepB");

        pipeline.AddStep(stepA);
        pipeline.AddStep(stepB);
        pipeline.AddStep(stepC);

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("Circular dependency", ex.Message);
        // Should mention the cycle
        Assert.True(ex.Message.Contains("stepA") || ex.Message.Contains("stepB") || ex.Message.Contains("stepC"),
            "Error message should mention at least one step in the cycle");
    }

    [Fact]
    public async Task ExecuteAsync_WithFailure_PreventsOtherStepsFromStarting()
    {
        // Test that when one step fails, other steps that haven't started yet don't start
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var step2Started = false;

        // Step 1 will fail after a short delay
        pipeline.AddStep("step1", async (context) =>
        {
            await Task.Delay(50);
            throw new InvalidOperationException("Step 1 failed");
        });

        // Step 2 depends on step1, so it definitely shouldn't start
        pipeline.AddStep("step2", async (context) =>
        {
            step2Started = true;
            await Task.CompletedTask;
        }, dependsOn: "step1");

        var context = CreateDeployingContext(builder.Build());

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();

        // Step 2 should never start because its dependency failed
        Assert.False(step2Started, "Step depending on failed step should not start");
    }

    [Fact]
    public async Task ExecuteAsync_WhenStepThrows_ReportsFailureToActivityReporter()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();
        var exceptionMessage = "Test exception for reporting";
        pipeline.AddStep("failing-step", async (context) =>
        {
            await Task.CompletedTask;
            throw new NotSupportedException(exceptionMessage);
        });

        var context = CreateDeployingContext(builder.Build());

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();

        // Assert - Verify the exception was thrown
        Assert.Contains("failing-step", ex.Message);
        Assert.Contains("failed", ex.Message);

        // Assert - Verify the step was created and completed with error
        var reporter = context.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);
        Assert.Contains("failing-step", reporter.CreatedSteps);
        Assert.Contains(reporter.CompletedSteps, step => step.StepTitle == "failing-step" && step.CompletionState == CompletionState.CompletedWithError);
    }

    [Fact]
    public async Task ExecuteAsync_WithDiamondDependency_ExecutesCorrectly()
    {
        // Diamond pattern: A -> B, A -> C, B -> D, C -> D
        // D should only start after both B and C complete
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executionOrder = new List<string>();
        var executionTimes = new Dictionary<string, DateTime>();

        pipeline.AddStep("A", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add("A"); executionTimes["A"] = DateTime.UtcNow; }
            await Task.Delay(10);
        });

        pipeline.AddStep("B", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add("B"); executionTimes["B"] = DateTime.UtcNow; }
            await Task.Delay(10);
        }, dependsOn: "A");

        pipeline.AddStep("C", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add("C"); executionTimes["C"] = DateTime.UtcNow; }
            await Task.Delay(10);
        }, dependsOn: "A");

        pipeline.AddStep("D", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add("D"); executionTimes["D"] = DateTime.UtcNow; }
            await Task.CompletedTask;
        }, dependsOn: new[] { "B", "C" });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(4, executionOrder.Count);

        // Verify execution order
        var aIndex = executionOrder.IndexOf("A");
        var bIndex = executionOrder.IndexOf("B");
        var cIndex = executionOrder.IndexOf("C");
        var dIndex = executionOrder.IndexOf("D");

        Assert.True(aIndex < bIndex, "A should execute before B");
        Assert.True(aIndex < cIndex, "A should execute before C");
        Assert.True(bIndex < dIndex, "B should execute before D");
        Assert.True(cIndex < dIndex, "C should execute before D");

        // Verify that D started after both B and C (not just one of them)
        Assert.True(executionTimes["D"] >= executionTimes["B"], "D should start after B completes");
        Assert.True(executionTimes["D"] >= executionTimes["C"], "D should start after C completes");
    }

    private static PipelineContext CreateDeployingContext(DistributedApplication app)
    {
        return new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<DistributedApplicationPipelineTests>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WithPipelineStepFactoryAnnotation_FactoryReceivesPipelineContextAndResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        IResource? capturedResource = null;
        PipelineContext? capturedPipelineContext = null;
        var executedSteps = new List<string>();

        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory((factoryContext) =>
            {
                capturedResource = factoryContext.Resource;
                capturedPipelineContext = factoryContext.PipelineContext;

                return new PipelineStep
                {
                    Name = "annotated-step",
                    Action = async (ctx) =>
                    {
                        lock (executedSteps) { executedSteps.Add("annotated-step"); }
                        await Task.CompletedTask;
                    }
                };
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.NotNull(capturedResource);
        Assert.Equal("test-resource", capturedResource.Name);
        Assert.NotNull(capturedPipelineContext);
        Assert.Same(context, capturedPipelineContext);
        Assert.Contains("annotated-step", executedSteps);
    }

    [Fact]
    public async Task WithPipelineStepFactory_SyncOverload_ExecutesStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory((factoryContext) => new PipelineStep
            {
                Name = "sync-step",
                Action = async (ctx) =>
                {
                    lock (executedSteps) { executedSteps.Add("sync-step"); }
                    await Task.CompletedTask;
                }
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Contains("sync-step", executedSteps);
    }

    [Fact]
    public async Task WithPipelineStepFactory_AsyncOverload_ExecutesStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory(async (factoryContext) =>
            {
                await Task.CompletedTask;
                return new PipelineStep
                {
                    Name = "async-step",
                    Action = async (ctx) =>
                    {
                        lock (executedSteps) { executedSteps.Add("async-step"); }
                        await Task.CompletedTask;
                    }
                };
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Contains("async-step", executedSteps);
    }

    [Fact]
    public async Task WithPipelineStepFactory_MultipleStepsSyncOverload_ExecutesAllSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory((factoryContext) =>
            [
                new PipelineStep
                {
                    Name = "sync-step-1",
                    Action = async (ctx) =>
                    {
                        lock (executedSteps) { executedSteps.Add("sync-step-1"); }
                        await Task.CompletedTask;
                    }
                },
                new PipelineStep
                {
                    Name = "sync-step-2",
                    Action = async (ctx) =>
                    {
                        lock (executedSteps) { executedSteps.Add("sync-step-2"); }
                        await Task.CompletedTask;
                    }
                }
            ]);

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Contains("sync-step-1", executedSteps);
        Assert.Contains("sync-step-2", executedSteps);
    }

    [Fact]
    public async Task WithPipelineStepFactory_MultipleStepsAsyncOverload_ExecutesAllSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineStepFactory(async (factoryContext) =>
            {
                await Task.CompletedTask;
                return
                [
                    new PipelineStep
                    {
                        Name = "async-step-1",
                        Action = async (ctx) =>
                        {
                            lock (executedSteps) { executedSteps.Add("async-step-1"); }
                            await Task.CompletedTask;
                        }
                    },
                    new PipelineStep
                    {
                        Name = "async-step-2",
                        Action = async (ctx) =>
                        {
                            lock (executedSteps) { executedSteps.Add("async-step-2"); }
                            await Task.CompletedTask;
                        }
                    }
                ];
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Contains("async-step-1", executedSteps);
        Assert.Contains("async-step-2", executedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_WithPipelineLoggerProvider_LogsToStepLogger()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();
        var loggedMessages = new List<string>();

        pipeline.AddStep("logging-step", (context) =>
        {
            // Get a logger from DI which should be the PipelineLogger
            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("TestCategory");

            logger.LogInformation("Test log message from pipeline step");
            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());

        // Act
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Assert
        var reporter = context.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);
        Assert.Contains("logging-step", reporter.CreatedSteps);
        Assert.Contains(reporter.LoggedMessages, log => log.Message == "Test log message from pipeline step" && log.LogLevel == LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_PipelineLoggerProvider_IsolatesLoggingBetweenSteps()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();
        var step1Logger = (ILogger?)null;
        var step2Logger = (ILogger?)null;

        pipeline.AddStep("step1", async (context) =>
        {
            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            step1Logger = loggerFactory.CreateLogger("Step1Category");

            // Verify this step has its own contextual logger
            Assert.Same(context.ReportingStep, PipelineLoggerProvider.CurrentStep);

            step1Logger.LogInformation("Message from step 1");
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", (context) =>
        {
            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            step2Logger = loggerFactory.CreateLogger("Step2Category");

            // Verify this step has its own contextual logger (different from step1)
            Assert.Same(context.ReportingStep, PipelineLoggerProvider.CurrentStep);

            step2Logger.LogInformation("Message from step 2");
            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());

        // Act
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Assert
        Assert.NotNull(step1Logger);
        Assert.NotNull(step2Logger);

        // Verify steps were created and completed
        var reporter = context.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);
        Assert.Contains("step1", reporter.CreatedSteps);
        Assert.Contains("step2", reporter.CreatedSteps);

        // Verify log messages were captured
        Assert.Contains(reporter.LoggedMessages, log => log.Message == "Message from step 1" && log.LogLevel == LogLevel.Information);
        Assert.Contains(reporter.LoggedMessages, log => log.Message == "Message from step 2" && log.LogLevel == LogLevel.Information);

        // After execution, current step should be null
        Assert.Null(PipelineLoggerProvider.CurrentStep);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStepFails_PipelineLoggerIsCleanedUp()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("failing-step", async (context) =>
        {
            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("FailingCategory");

            logger.LogInformation("About to fail");

            throw new InvalidOperationException("Test failure");
        });

        var context = CreateDeployingContext(builder.Build());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();

        // Verify the failing step was created and completed with error
        var reporter = context.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);
        Assert.Contains("failing-step", reporter.CreatedSteps);
        Assert.Contains(reporter.CompletedSteps, step => step.StepTitle == "failing-step" && step.CompletionState == CompletionState.CompletedWithError);

        // Verify the log message before failure was captured
        Assert.Contains(reporter.LoggedMessages, log => log.Message == "About to fail" && log.LogLevel == LogLevel.Information);

        // Verify logger is cleaned up even after failure
        Assert.Null(PipelineLoggerProvider.CurrentStep);
    }

    [Fact]
    public async Task ExecuteAsync_PipelineLoggerProvider_PreservesLoggerAfterStepCompletion()
    {
        // This test verifies that each step gets a clean logger context
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();
        var capturedSteps = new List<IReportingStep?>();

        for (var i = 1; i <= 3; i++)
        {
            var stepNumber = i; // Capture for closure
            pipeline.AddStep($"step{stepNumber}", (context) =>
            {
                // Capture the current step for this step
                lock (capturedSteps)
                {
                    capturedSteps.Add(PipelineLoggerProvider.CurrentStep);
                }

                var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger($"Step{stepNumber}");

                logger.LogInformation("Executing step {stepNumber}", stepNumber);
                return Task.CompletedTask;
            });
        }

        var context = CreateDeployingContext(builder.Build());

        // Act
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Assert
        Assert.Equal(3, capturedSteps.Count);

        // Each step should have had a different step context
        // (We can't easily verify they're different instances since they're created per step,
        // but we can verify none of them are null during execution)
        foreach (var step in capturedSteps)
        {
            Assert.NotNull(step);
        }

        // Verify all steps were created
        var reporter = context.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);
        Assert.Contains("step1", reporter.CreatedSteps);
        Assert.Contains("step2", reporter.CreatedSteps);
        Assert.Contains("step3", reporter.CreatedSteps);

        // Verify all log messages were captured
        Assert.Contains(reporter.LoggedMessages, log => log.Message.Contains("Executing step 1") && log.LogLevel == LogLevel.Information);
        Assert.Contains(reporter.LoggedMessages, log => log.Message.Contains("Executing step 2") && log.LogLevel == LogLevel.Information);
        Assert.Contains(reporter.LoggedMessages, log => log.Message.Contains("Executing step 3") && log.LogLevel == LogLevel.Information);

        // After all steps complete, should be back to null
        Assert.Null(PipelineLoggerProvider.CurrentStep);
    }

    [Theory]
    [InlineData("Debug", new[] { "Debug", "Information", "Warning" }, new[] { "Debug", "Information", "Warning" })]
    [InlineData("Information", new[] { "Debug", "Information", "Warning" }, new[] { "Information", "Warning" })]
    [InlineData("Warning", new[] { "Debug", "Information", "Warning" }, new[] { "Warning" })]
    [InlineData("Error", new[] { "Debug", "Information", "Warning" }, new string[0])]
    public async Task ExecuteAsync_PipelineLoggerProvider_RespectsPipelineLogLevelConfiguration(
        string configuredLogLevel,
        string[] loggedLevels,
        string[] expectedFilteredLevels)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, logLevel: configuredLogLevel, step: "logging-step").WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("logging-step", (context) =>
        {
            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("TestCategory");

            // Log messages at different levels
            foreach (var level in loggedLevels)
            {
                switch (level)
                {
                    case "Debug":
                        logger.LogDebug($"Debug message");
                        break;
                    case "Information":
                        logger.LogInformation($"Information message");
                        break;
                    case "Warning":
                        logger.LogWarning($"Warning message");
                        break;
                }
            }

            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());

        // Act
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Assert - Verify that only the expected log levels are present
        var reporter = context.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);
        var capturedLogs = reporter.LoggedMessages.Where(log => log.Message.EndsWith(" message")).ToList();
        Assert.Equal(expectedFilteredLevels.Length, capturedLogs.Count);

        // Verify each expected log level appears exactly once
        foreach (var expectedLevel in expectedFilteredLevels)
        {
            Assert.Contains(capturedLogs, log =>
                log.LogLevel.ToString() == expectedLevel &&
                log.Message == $"{expectedLevel} message");
        }
    }

    [Fact]
    public async Task PipelineStep_WithTags_StoresTagsCorrectly()
    {
        var step = new PipelineStep
        {
            Name = "test-step",
            Action = async (ctx) => await Task.CompletedTask,
            Tags = ["tag1", "tag2"]
        };

        Assert.Equal(2, step.Tags.Count);
        Assert.Contains("tag1", step.Tags);
        Assert.Contains("tag2", step.Tags);
    }

    [Fact]
    public async Task ExecuteAsync_WithConfigurationCallback_ExecutesCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var callbackExecuted = false;
        var capturedSteps = new List<PipelineStep>();

        pipeline.AddStep("step1", async (context) => await Task.CompletedTask);
        pipeline.AddStep("step2", async (context) => await Task.CompletedTask);

        pipeline.AddPipelineConfiguration((configContext) =>
        {
            callbackExecuted = true;
            capturedSteps.AddRange(configContext.Steps);
            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(callbackExecuted);
        Assert.Equal(12, capturedSteps.Count); // Updated to account for all default steps including process-parameters, push, push-prereq
        Assert.Contains(capturedSteps, s => s.Name == "deploy");
        Assert.Contains(capturedSteps, s => s.Name == "process-parameters");
        Assert.Contains(capturedSteps, s => s.Name == "deploy-prereq");
        Assert.Contains(capturedSteps, s => s.Name == "build");
        Assert.Contains(capturedSteps, s => s.Name == "build-prereq");
        Assert.Contains(capturedSteps, s => s.Name == "push");
        Assert.Contains(capturedSteps, s => s.Name == "push-prereq");
        Assert.Contains(capturedSteps, s => s.Name == "publish");
        Assert.Contains(capturedSteps, s => s.Name == "publish-prereq");
        Assert.Contains(capturedSteps, s => s.Name == "diagnostics");
        Assert.Contains(capturedSteps, s => s.Name == "step1");
        Assert.Contains(capturedSteps, s => s.Name == "step2");
    }

    [Fact]
    public async Task ExecuteAsync_ConfigurationCallback_CanModifyDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executionOrder = new List<string>();

        pipeline.AddStep("step1", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add("step1"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executionOrder) { executionOrder.Add("step2"); }
            await Task.CompletedTask;
        });

        pipeline.AddPipelineConfiguration((configContext) =>
        {
            var step1 = configContext.Steps.First(s => s.Name == "step1");
            var step2 = configContext.Steps.First(s => s.Name == "step2");
            step2.DependsOn(step1);
            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(["step1", "step2"], executionOrder);
    }

    [Fact]
    public async Task PipelineConfigurationContext_GetStepsByTag_ReturnsCorrectSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var foundSteps = new List<PipelineStep>();

        pipeline.AddStep(new PipelineStep
        {
            Name = "step1",
            Action = async (ctx) => await Task.CompletedTask,
            Tags = ["test-tag"]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step2",
            Action = async (ctx) => await Task.CompletedTask,
            Tags = ["test-tag", "another-tag"]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "step3",
            Action = async (ctx) => await Task.CompletedTask,
            Tags = ["different-tag"]
        });

        pipeline.AddPipelineConfiguration((configContext) =>
        {
            foundSteps.AddRange(configContext.GetSteps("test-tag"));
            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(2, foundSteps.Count);
        Assert.Contains(foundSteps, s => s.Name == "step1");
        Assert.Contains(foundSteps, s => s.Name == "step2");
        Assert.DoesNotContain(foundSteps, s => s.Name == "step3");
    }

    [Fact]
    public async Task PipelineConfigurationContext_GetStepsByResource_ReturnsCorrectSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var foundSteps = new List<PipelineStep>();
        IResource? targetResource = null;

        var resource1 = builder.AddResource(new CustomResource("resource1"))
            .WithPipelineStepFactory((factoryContext) =>
            [
                new PipelineStep
                {
                    Name = "resource1-step1",
                    Action = async (ctx) => await Task.CompletedTask
                },
                new PipelineStep
                {
                    Name = "resource1-step2",
                    Action = async (ctx) => await Task.CompletedTask
                }
            ]);

        var resource2 = builder.AddResource(new CustomResource("resource2"))
            .WithPipelineStepFactory((factoryContext) =>
            {
                targetResource = factoryContext.Resource;
                return new PipelineStep
                {
                    Name = "resource2-step1",
                    Action = async (ctx) => await Task.CompletedTask
                };
            })
            .WithPipelineConfiguration((configContext) =>
            {
                var resource2Instance = configContext.Model.Resources.FirstOrDefault(r => r.Name == "resource2");
                if (resource2Instance != null)
                {
                    foundSteps.AddRange(configContext.GetSteps(resource2Instance));
                }
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Single(foundSteps);
        Assert.Contains(foundSteps, s => s.Name == "resource2-step1");
    }

    [Fact]
    public async Task PipelineConfigurationContext_GetStepsByResourceAndTag_ReturnsCorrectSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var foundSteps = new List<PipelineStep>();

        var resource1 = builder.AddResource(new CustomResource("resource1"))
            .WithPipelineStepFactory((factoryContext) =>
            [
                new PipelineStep
                {
                    Name = "resource1-step1",
                    Action = async (ctx) => await Task.CompletedTask,
                    Tags = ["build"]
                },
                new PipelineStep
                {
                    Name = "resource1-step2",
                    Action = async (ctx) => await Task.CompletedTask,
                    Tags = ["deploy"]
                }
            ])
            .WithPipelineConfiguration((configContext) =>
            {
                var resource1Instance = configContext.Model.Resources.FirstOrDefault(r => r.Name == "resource1");
                if (resource1Instance != null)
                {
                    foundSteps.AddRange(configContext.GetSteps(resource1Instance, "build"));
                }
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Single(foundSteps);
        Assert.Contains(foundSteps, s => s.Name == "resource1-step1");
    }

    [Fact]
    public async Task WithPipelineConfiguration_AsyncOverload_ExecutesCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var callbackExecuted = false;

        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineConfiguration(async (configContext) =>
            {
                await Task.CompletedTask;
                callbackExecuted = true;
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(callbackExecuted);
    }

    [Fact]
    public async Task WithPipelineConfiguration_SyncOverload_ExecutesCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var callbackExecuted = false;

        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineConfiguration((configContext) =>
            {
                callbackExecuted = true;
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(callbackExecuted);
    }

    [Fact]
    public async Task ConfigurationCallback_CanAccessModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        IResource? capturedResource = null;

        var resource = builder.AddResource(new CustomResource("test-resource"))
            .WithPipelineConfiguration((configContext) =>
            {
                capturedResource = configContext.Model.Resources.FirstOrDefault(r => r.Name == "test-resource");
            });

        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.NotNull(capturedResource);
        Assert.Equal("test-resource", capturedResource.Name);
    }

    [Fact]
    public async Task ConfigurationCallback_ExecutesAfterStepCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var allStepsAvailable = false;

        builder.AddResource(new CustomResource("resource1"))
            .WithPipelineStepFactory((factoryContext) => new PipelineStep
            {
                Name = "resource1-step",
                Action = async (ctx) => await Task.CompletedTask
            });

        builder.AddResource(new CustomResource("resource2"))
            .WithPipelineConfiguration((configContext) =>
            {
                allStepsAvailable = configContext.Steps.Any(s => s.Name == "resource1-step");
            });

        var pipeline = new DistributedApplicationPipeline();
        pipeline.AddStep("direct-step", async (context) => await Task.CompletedTask);

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.True(allStepsAvailable, "Configuration phase should have access to all collected steps");
    }

    [Fact]
    public void WellKnownPipelineTags_ConstantsAccessible()
    {
        Assert.Equal("provision-infra", WellKnownPipelineTags.ProvisionInfrastructure);
        Assert.Equal("build-compute", WellKnownPipelineTags.BuildCompute);
        Assert.Equal("deploy-compute", WellKnownPipelineTags.DeployCompute);
    }

    [Fact]
    public async Task ConfigurationCallback_CanCreateComplexDependencyRelationships()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        var pipeline = new DistributedApplicationPipeline();

        var executionOrder = new List<string>();

        pipeline.AddStep(new PipelineStep
        {
            Name = "provision1",
            Action = async (ctx) =>
            {
                lock (executionOrder) { executionOrder.Add("provision1"); }
                await Task.CompletedTask;
            },
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "provision2",
            Action = async (ctx) =>
            {
                lock (executionOrder) { executionOrder.Add("provision2"); }
                await Task.CompletedTask;
            },
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "build1",
            Action = async (ctx) =>
            {
                lock (executionOrder) { executionOrder.Add("build1"); }
                await Task.CompletedTask;
            },
            Tags = [WellKnownPipelineTags.BuildCompute]
        });

        pipeline.AddStep(new PipelineStep
        {
            Name = "deploy1",
            Action = async (ctx) =>
            {
                lock (executionOrder) { executionOrder.Add("deploy1"); }
                await Task.CompletedTask;
            },
            Tags = [WellKnownPipelineTags.DeployCompute]
        });

        pipeline.AddPipelineConfiguration((configContext) =>
        {
            var provisionSteps = configContext.GetSteps(WellKnownPipelineTags.ProvisionInfrastructure).ToList();
            var buildSteps = configContext.GetSteps(WellKnownPipelineTags.BuildCompute).ToList();
            var deploySteps = configContext.GetSteps(WellKnownPipelineTags.DeployCompute).ToList();

            foreach (var buildStep in buildSteps)
            {
                foreach (var provisionStep in provisionSteps)
                {
                    buildStep.DependsOn(provisionStep);
                }
            }

            foreach (var deployStep in deploySteps)
            {
                foreach (var buildStep in buildSteps)
                {
                    deployStep.DependsOn(buildStep);
                }
            }

            return Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        var provision1Index = executionOrder.IndexOf("provision1");
        var provision2Index = executionOrder.IndexOf("provision2");
        var build1Index = executionOrder.IndexOf("build1");
        var deploy1Index = executionOrder.IndexOf("deploy1");

        Assert.True(provision1Index < build1Index, "provision1 should execute before build1");
        Assert.True(provision2Index < build1Index, "provision2 should execute before build1");
        Assert.True(build1Index < deploy1Index, "build1 should execute before deploy1");
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentStepFilter_ThrowsInvalidOperationExceptionWithAvailableSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "non-existent-step").WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();

        pipeline.AddStep("step1", async (context) => await Task.CompletedTask);
        pipeline.AddStep("step2", async (context) => await Task.CompletedTask);
        pipeline.AddStep("step3", async (context) => await Task.CompletedTask);

        var context = CreateDeployingContext(builder.Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context)).DefaultTimeout();
        Assert.Contains("Step 'non-existent-step' not found in pipeline", ex.Message);
        Assert.Contains("Available steps:", ex.Message);
        Assert.Contains("'step1'", ex.Message);
        Assert.Contains("'step2'", ex.Message);
        Assert.Contains("'step3'", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithStepFilterAndComplexDependencies_ExecutesTransitiveClosure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "step5").WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();

        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step2", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step2"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        }, dependsOn: "step1");

        pipeline.AddStep("step4", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step4"); }
            await Task.CompletedTask;
        }, dependsOn: "step2");

        pipeline.AddStep("step5", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step5"); }
            await Task.CompletedTask;
        }, dependsOn: new[] { "step3", "step4" });

        pipeline.AddStep("step6", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step6"); }
            await Task.CompletedTask;
        });

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Equal(5, executedSteps.Count);
        Assert.Contains("step1", executedSteps);
        Assert.Contains("step2", executedSteps);
        Assert.Contains("step3", executedSteps);
        Assert.Contains("step4", executedSteps);
        Assert.Contains("step5", executedSteps);
        Assert.DoesNotContain("step6", executedSteps);

        var step1Index = executedSteps.IndexOf("step1");
        var step2Index = executedSteps.IndexOf("step2");
        var step3Index = executedSteps.IndexOf("step3");
        var step4Index = executedSteps.IndexOf("step4");
        var step5Index = executedSteps.IndexOf("step5");

        Assert.True(step1Index < step3Index, "step1 should execute before step3");
        Assert.True(step2Index < step4Index, "step2 should execute before step4");
        Assert.True(step3Index < step5Index, "step3 should execute before step5");
        Assert.True(step4Index < step5Index, "step4 should execute before step5");
    }

    [Fact]
    public async Task ExecuteAsync_WithStepFilterForIndependentStep_ExecutesOnlyThatStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "independent-step").WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var pipeline = new DistributedApplicationPipeline();

        var executedSteps = new List<string>();

        pipeline.AddStep("step1", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step1"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("independent-step", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("independent-step"); }
            await Task.CompletedTask;
        });

        pipeline.AddStep("step3", async (context) =>
        {
            lock (executedSteps) { executedSteps.Add("step3"); }
            await Task.CompletedTask;
        }, dependsOn: "step1");

        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        Assert.Single(executedSteps);
        Assert.Contains("independent-step", executedSteps);
        Assert.DoesNotContain("step1", executedSteps);
        Assert.DoesNotContain("step3", executedSteps);
    }

    [Fact]
    public async Task FilterStepsForExecution_WithRequiredBy_IncludesTransitiveDependencies()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        var executedSteps = new List<string>();
        var lockObject = new object();
        var pipeline = new DistributedApplicationPipeline();

        // The pipeline initializes with a "deploy" step by default, but we need to track when it executes
        // So we need to add our own deploy step that tracks execution
        // First, let's remove the default deploy step by not adding it, and add our own

        // Create steps: provision-resource1 and provision-resource2 are required by provision-infra
        // When we execute "my-deploy-step", we should get: provision-resource1, provision-resource2, provision-infra, and my-deploy-step
        pipeline.AddStep("provision-resource1", (context) =>
        {
            lock (lockObject)
            {
                executedSteps.Add("provision-resource1");
            }
            return Task.CompletedTask;
        }, requiredBy: "provision-infra");

        pipeline.AddStep("provision-resource2", (context) =>
        {
            lock (lockObject)
            {
                executedSteps.Add("provision-resource2");
            }
            return Task.CompletedTask;
        }, requiredBy: "provision-infra");

        pipeline.AddStep("provision-infra", (context) =>
        {
            lock (lockObject)
            {
                executedSteps.Add("provision-infra");
            }
            return Task.CompletedTask;
        }, requiredBy: "my-deploy-step");

        pipeline.AddStep("my-deploy-step", (context) =>
        {
            lock (lockObject)
            {
                executedSteps.Add("my-deploy-step");
            }
            return Task.CompletedTask;
        });

        // Act - execute with --step my-deploy-step filter
        builder.Services.Configure<PipelineOptions>(options => options.Step = "my-deploy-step");
        var context = CreateDeployingContext(builder.Build());
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Assert - all steps should have been executed
        Assert.Contains("provision-resource1", executedSteps);
        Assert.Contains("provision-resource2", executedSteps);
        Assert.Contains("provision-infra", executedSteps);
        Assert.Contains("my-deploy-step", executedSteps);
        Assert.Equal(4, executedSteps.Count);
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/13083")]
    public async Task ProcessParametersStep_ValidatesBehavior()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);

        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();
        
        // Add a parameter with a default value to trigger parameter processing
        builder.AddParameter("test-param", () => "default-value");

        var pipeline = new DistributedApplicationPipeline();
        
        var executionTimes = new Dictionary<string, DateTime>();
        var lockObject = new object();
        PipelineStep? parameterPromptingStep = null;
        PipelineStep? deployPrereqStep = null;
        PipelineStep? buildPrereqStep = null;
        PipelineStep? publishPrereqStep = null;
        
        // Capture steps and track execution order
        pipeline.AddPipelineConfiguration((configContext) =>
        {
            parameterPromptingStep = configContext.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.ProcessParameters);
            deployPrereqStep = configContext.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.DeployPrereq);
            buildPrereqStep = configContext.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.BuildPrereq);
            publishPrereqStep = configContext.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.PublishPrereq);
            return Task.CompletedTask;
        });
        
        // Add steps that depend on ProcessParameters and DeployPrereq to track their execution
        pipeline.AddStep("after-param-prompting", (context) =>
        {
            lock (lockObject) { executionTimes[WellKnownPipelineSteps.ProcessParameters] = DateTime.UtcNow; }
            return Task.CompletedTask;
        }, dependsOn: WellKnownPipelineSteps.ProcessParameters);
        
        pipeline.AddStep("after-deploy-prereq", (context) =>
        {
            lock (lockObject) { executionTimes[WellKnownPipelineSteps.DeployPrereq] = DateTime.UtcNow; }
            return Task.CompletedTask;
        }, dependsOn: WellKnownPipelineSteps.DeployPrereq);

        var context = CreateDeployingContext(builder.Build());

        // Act
        await pipeline.ExecuteAsync(context).DefaultTimeout();

        // Assert - Step exists in pipeline
        Assert.NotNull(parameterPromptingStep);
        Assert.NotNull(deployPrereqStep);
        Assert.NotNull(buildPrereqStep);
        Assert.NotNull(publishPrereqStep);
        
        // Assert - Dependency relationships are configured correctly
        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, parameterPromptingStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.BuildPrereq, parameterPromptingStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.PublishPrereq, parameterPromptingStep.RequiredBySteps);
        
        // Assert - Execution order is correct (ProcessParameters before DeployPrereq)
        Assert.True(executionTimes.ContainsKey(WellKnownPipelineSteps.ProcessParameters));
        Assert.True(executionTimes.ContainsKey(WellKnownPipelineSteps.DeployPrereq));
        Assert.True(executionTimes[WellKnownPipelineSteps.ProcessParameters] < executionTimes[WellKnownPipelineSteps.DeployPrereq], 
            "ProcessParameters should complete before DeployPrereq");
        
        // Assert - Parameters are processed
        var paramResource = builder.Resources.OfType<ParameterResource>().FirstOrDefault(p => p.Name == "test-param");
        Assert.NotNull(paramResource);
        Assert.NotNull(paramResource.WaitForValueTcs);
        Assert.True(paramResource.WaitForValueTcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PushPrereq_SkipsRegistryCheckForNonDockerImageFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PushPrereq).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        // Add a project that requires build and push
        var project = builder.AddProject<DummyProject>("test-project", launchProfileName: null);

        // Configure the project to save as archive (not push to registry)
        project.WithContainerBuildOptions(ctx =>
        {
            ctx.Destination = Aspire.Hosting.Publishing.ContainerImageDestination.Archive;
            ctx.OutputPath = "/tmp/output";
        });

        using var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(app);

        // Act & Assert - Should not throw an exception even though no registry is configured
        // because the Destination is Archive, not Registry
        await pipeline.ExecuteAsync(context).DefaultTimeout();
    }

    [Fact]
    public async Task PushPrereq_ThrowsForDockerImageFormatWithoutRegistry()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PushPrereq).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        // Add a project that requires build and push
        var project = builder.AddProject<DummyProject>("test-project", launchProfileName: null);

        // Configure the project to push to registry
        project.WithContainerBuildOptions(ctx =>
        {
            ctx.Destination = Aspire.Hosting.Publishing.ContainerImageDestination.Registry;
        });

        using var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(app);

        // Act & Assert - Should throw an exception because Registry destination requires a registry
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(context).DefaultTimeout());

        Assert.Contains("no container registry is available", exception.Message);
        Assert.Contains("test-project", exception.Message);
    }

    [Fact]
    public async Task PushPrereq_ThrowsForDefaultImageFormatWithoutRegistry()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PushPrereq).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        // Add a project that requires build and push without specifying Destination
        var project = builder.AddProject<DummyProject>("test-project", launchProfileName: null);

        using var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(app);

        // Act & Assert - Should throw an exception because default (null) Destination is treated as Registry
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(context).DefaultTimeout());

        Assert.Contains("no container registry is available", exception.Message);
        Assert.Contains("test-project", exception.Message);
    }

    [Fact]
    public async Task PushPrereq_ThrowsForArchiveDestinationWithoutOutputPath()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PushPrereq).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        // Add a project that requires build and push
        var project = builder.AddProject<DummyProject>("test-project", launchProfileName: null);

        // Configure the project to save as archive but without OutputPath
        project.WithContainerBuildOptions(ctx =>
        {
            ctx.Destination = Aspire.Hosting.Publishing.ContainerImageDestination.Archive;
            // OutputPath is not set
        });

        using var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(app);

        // Act & Assert - Should throw an exception because Archive destination requires OutputPath
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(context).DefaultTimeout());

        Assert.Contains("Destination set to Archive but OutputPath is not configured", exception.Message);
        Assert.Contains("test-project", exception.Message);
    }

    [Fact]
    public async Task PushPrereq_SkipsExcludedFromManifestResources()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PushPrereq).WithTestAndResourceLogging(testOutputHelper);
        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        // Add a container registry so non-excluded projects can push
        var registry = builder.AddContainerRegistry("test-registry", "registry.example.com");

        // Add a project that is NOT excluded - this should require the registry
        var includedProject = builder.AddProject<DummyProject>("included-project", launchProfileName: null)
            .WithContainerRegistry(registry);

        // Add a project that IS excluded - this should be skipped entirely
        var excludedProject = builder.AddProject<DummyProject>("excluded-project", launchProfileName: null)
            .ExcludeFromManifest();

        using var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = CreateDeployingContext(app);

        // Act & Assert - Should not throw an exception
        // The included project should use the registry, the excluded project should be skipped
        await pipeline.ExecuteAsync(context).DefaultTimeout();
    }

    private sealed class DummyProject : IProjectMetadata
    {
        public string ProjectPath => "dummy.csproj";
    }

    private sealed class CustomResource(string name) : Resource(name)
    {
    }
}
