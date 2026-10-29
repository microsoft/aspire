// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Pipelines;

public class PipelineOutputDiffingLifetimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "none")]
    [InlineData(false, "before")]
    [InlineData(false, "writer")]
    [InlineData(false, "after")]
    [InlineData(false, "compare")]
    [InlineData(false, "cancel")]
    [InlineData(true, "none")]
    [InlineData(true, "before")]
    [InlineData(true, "writer")]
    [InlineData(true, "after")]
    [InlineData(true, "compare")]
    [InlineData(true, "cancel")]
    public async Task NextRunResolvesFreshDifferAfterSuccessFailureOrCancellation(bool sequential, string failure)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: workspace.Path, step: "publish-and-diff");
        var instances = new List<LifetimeDiffer>();
        var firstRun = true;
        var writerCalls = 0;
        builder.Services.AddTransient<IPipelineOutputDiffer>(_ =>
        {
            var differ = new LifetimeDiffer(instances.Count == 0 ? failure : "none");
            instances.Add(differ);
            return differ;
        });
        builder.Pipeline.WithOutputDiffing();
        builder.Pipeline.AddStep("lifetime-writer", context =>
        {
            writerCalls++;
            if (context.PipelineContext.SelectedSteps.Any(step => step.Name == "publish-and-diff"))
            {
                Assert.Equal(new[] { "before" }, instances[^1].Phases);
            }
            if (firstRun && failure == "writer")
            {
                throw new IOException("lifetime-writer");
            }
            return Task.CompletedTask;
        }, requiredBy: WellKnownPipelineSteps.Publish);
        using var app = builder.Build();
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = NewContext();
        Assert.Empty(instances);
        Assert.Equal(0, writerCalls);

        if (failure == "none")
        {
            await Run(pipeline, context, "publish-and-diff");
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Run(pipeline, context, "publish-and-diff"));
            Assert.Contains("lifetime-" + failure, error.ToString());
        }
        firstRun = false;
        var first = Assert.Single(instances);
        var expectedFirstPhases = failure switch
        {
            "before" or "cancel" or "writer" => new[] { "before" },
            "after" => new[] { "before", "after" },
            _ => new[] { "before", "after", "compare" }
        };
        Assert.Equal(expectedFirstPhases, first.Phases);
        Assert.Equal(failure is "before" or "cancel" ? 0 : 1, writerCalls);
        Assert.False(first.Disposed);

        // Ordinary publish after the first run must not resolve another differ.
        await Run(pipeline, NewContext(), WellKnownPipelineSteps.Publish);
        Assert.Single(instances);
        Assert.Equal(expectedFirstPhases, first.Phases);

        await Run(pipeline, context, "publish-and-diff");
        Assert.Equal(2, instances.Count);
        var second = instances[1];
        Assert.NotSame(first, second);
        Assert.Equal(expectedFirstPhases, first.Phases);
        Assert.Equal(new[] { "before", "after", "compare" }, second.Phases);
        Assert.All(instances, differ => Assert.False(differ.Disposed));
        app.Dispose();
        Assert.All(instances, differ => Assert.True(differ.Disposed));

        PipelineContext NewContext() => new(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Task Run(DistributedApplicationPipeline selectedPipeline, PipelineContext selectedContext, string target)
        {
            app.Services.GetRequiredService<IOptions<PipelineOptions>>().Value.Step = target;
            return sequential
                ? selectedPipeline.ExecuteStepSequentiallyAsync(target, selectedContext)
                : selectedPipeline.ExecuteAsync(selectedContext);
        }
    }

    [Fact]
    public async Task CloneResolvesItsOwnDifferWithoutReusingCompletedPhases()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: workspace.Path, step: "publish-and-diff");
        var instances = new List<LifetimeDiffer>();
        builder.Services.AddTransient<IPipelineOutputDiffer>(_ =>
        {
            var differ = new LifetimeDiffer("none");
            instances.Add(differ);
            return differ;
        });
        builder.Pipeline.WithOutputDiffing();
        using var app = builder.Build();
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Empty(instances);

        await pipeline.ExecuteAsync(context);
        var first = Assert.Single(instances);
        Assert.Equal(new[] { "before", "after", "compare" }, first.Phases);
        await pipeline.Clone().ExecuteAsync(context);

        Assert.Equal(2, instances.Count);
        Assert.NotSame(first, instances[1]);
        Assert.All(instances, differ => Assert.Equal(new[] { "before", "after", "compare" }, differ.Phases));
    }

    private sealed class LifetimeDiffer(string failure) : IPipelineOutputDiffer, IDisposable
    {
        public List<string> Phases { get; } = [];
        public bool Disposed { get; private set; }

        public Task CaptureBeforeAsync(string outputDirectory, CancellationToken ct)
        {
            Assert.False(Disposed);
            Assert.Empty(Phases);
            Assert.NotEmpty(outputDirectory);
            Phases.Add("before");
            if (failure == "before")
            {
                throw new IOException("lifetime-before");
            }
            if (failure == "cancel")
            {
                throw new OperationCanceledException("lifetime-cancel", new CancellationToken(canceled: true));
            }
            return Task.CompletedTask;
        }

        public Task CaptureAfterAsync(string outputDirectory, CancellationToken ct)
        {
            Assert.False(Disposed);
            Assert.Equal(new[] { "before" }, Phases);
            Assert.NotEmpty(outputDirectory);
            Phases.Add("after");
            if (failure == "after")
            {
                throw new IOException("lifetime-after");
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(CancellationToken ct)
        {
            Assert.False(Disposed);
            Assert.Equal(new[] { "before", "after" }, Phases);
            Phases.Add("compare");
            if (failure == "compare")
            {
                throw new IOException("lifetime-compare");
            }
            return Task.FromResult<IReadOnlyList<PipelineOutputChange>>([]);
        }

        public void Dispose() => Disposed = true;
    }
}
