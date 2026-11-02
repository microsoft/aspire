// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

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
    public async Task NextRunResolvesFreshProvidersAfterSuccessFailureOrCancellation(bool sequential, string failure)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotters = new List<LifetimeSnapshotter>();
        var comparers = new List<LifetimeComparer>();
        var firstRun = true;
        var writerCalls = 0;
        builder.Services.AddTransient<IPipelineOutputSnapshotter<LifetimeSnapshot>>(_ =>
        {
            var snapshotter = new LifetimeSnapshotter(snapshotters.Count == 0 ? failure : "none");
            snapshotters.Add(snapshotter);
            return snapshotter;
        });
        builder.Services.AddTransient<IPipelineOutputComparer<LifetimeSnapshot>>(_ =>
        {
            Assert.Equal(new[] { "before", "after" }, snapshotters[^1].Events);
            var comparer = new LifetimeComparer(firstRun ? failure : "none");
            comparers.Add(comparer);
            return comparer;
        });
        builder.WithOutputDiffing<LifetimeSnapshot, LifetimeSnapshotter, LifetimeComparer>();
        builder.Pipeline.AddStep("lifetime-writer", context =>
        {
            writerCalls++;
            if (context.PipelineContext.SelectedSteps.Any(step => step.Name == "publish-and-diff"))
            {
                Assert.Equal(new[] { "before" }, snapshotters[^1].Events);
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
        Assert.Empty(snapshotters);
        Assert.Empty(comparers);
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
        var first = Assert.Single(snapshotters);
        var expectedFirstEvents = failure switch
        {
            "before" or "cancel" or "writer" => new[] { "before" },
            "after" => new[] { "before", "after" },
            _ => new[] { "before", "after", "compare" }
        };
        var firstComparerCount = failure is "none" or "compare" ? 1 : 0;
        Assert.Equal(expectedFirstEvents, first.Events);
        Assert.Equal(firstComparerCount, comparers.Count);
        Assert.All(comparers, comparer => Assert.Equal(1, comparer.Comparisons));
        Assert.Equal(failure is "before" or "cancel" ? 0 : 1, writerCalls);
        Assert.False(first.Disposed);
        Assert.All(comparers, comparer => Assert.False(comparer.Disposed));

        // Ordinary publish must not construct either provider or reuse a completed capture.
        await Run(pipeline, NewContext(), WellKnownPipelineSteps.Publish);
        Assert.Single(snapshotters);
        Assert.Equal(firstComparerCount, comparers.Count);
        Assert.Equal(expectedFirstEvents, first.Events);

        await Run(pipeline, context, "publish-and-diff");
        Assert.Equal(2, snapshotters.Count);
        var second = snapshotters[1];
        Assert.NotSame(first, second);
        Assert.Equal(expectedFirstEvents, first.Events);
        Assert.Equal(new[] { "before", "after", "compare" }, second.Events);
        Assert.Equal(firstComparerCount + 1, comparers.Count);
        if (firstComparerCount == 1)
        {
            Assert.NotSame(comparers[0], comparers[1]);
        }
        Assert.All(comparers, comparer => Assert.Equal(1, comparer.Comparisons));
        Assert.All(snapshotters, snapshotter => Assert.False(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.False(comparer.Disposed));
        app.Dispose();
        Assert.All(snapshotters, snapshotter => Assert.True(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.True(comparer.Disposed));

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
    public async Task CloneResolvesItsOwnProvidersWithoutReusingCompletedSnapshots()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotters = new List<LifetimeSnapshotter>();
        var comparers = new List<LifetimeComparer>();
        builder.Services.AddTransient<IPipelineOutputSnapshotter<LifetimeSnapshot>>(_ =>
        {
            var snapshotter = new LifetimeSnapshotter("none");
            snapshotters.Add(snapshotter);
            return snapshotter;
        });
        builder.Services.AddTransient<IPipelineOutputComparer<LifetimeSnapshot>>(_ =>
        {
            Assert.Equal(new[] { "before", "after" }, snapshotters[^1].Events);
            var comparer = new LifetimeComparer("none");
            comparers.Add(comparer);
            return comparer;
        });
        builder.WithOutputDiffing<LifetimeSnapshot, LifetimeSnapshotter, LifetimeComparer>();
        using var app = builder.Build();
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Empty(snapshotters);
        Assert.Empty(comparers);

        await pipeline.ExecuteAsync(context);
        var first = Assert.Single(snapshotters);
        var firstComparer = Assert.Single(comparers);
        Assert.Equal(new[] { "before", "after", "compare" }, first.Events);
        await pipeline.Clone().ExecuteAsync(context);

        Assert.Equal(2, snapshotters.Count);
        Assert.Equal(2, comparers.Count);
        Assert.NotSame(first, snapshotters[1]);
        Assert.NotSame(firstComparer, comparers[1]);
        Assert.All(snapshotters, snapshotter => Assert.Equal(new[] { "before", "after", "compare" }, snapshotter.Events));
        Assert.All(comparers, comparer => Assert.Equal(1, comparer.Comparisons));
        Assert.All(snapshotters, snapshotter => Assert.False(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.False(comparer.Disposed));
        app.Dispose();
        Assert.All(snapshotters, snapshotter => Assert.True(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.True(comparer.Disposed));
    }

    private sealed record LifetimeSnapshot(LifetimeSnapshotter Owner, int Capture);

    private sealed class LifetimeSnapshotter(string failure) : IPipelineOutputSnapshotter<LifetimeSnapshot>, IDisposable
    {
        public List<string> Events { get; } = [];
        public bool Disposed { get; private set; }

        public Task<LifetimeSnapshot> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            Assert.NotEmpty(outputDirectory);
            Assert.InRange(Events.Count, 0, 1);
            var first = Events.Count == 0;
            var capture = first ? "before" : "after";
            Events.Add(capture);
            if (failure == capture)
            {
                throw new IOException("lifetime-" + capture);
            }
            if (first && failure == "cancel")
            {
                throw new OperationCanceledException("lifetime-cancel", new CancellationToken(canceled: true));
            }

            return Task.FromResult(new LifetimeSnapshot(this, Events.Count));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class LifetimeComparer(string failure) : IPipelineOutputComparer<LifetimeSnapshot>, IDisposable
    {
        public int Comparisons { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(LifetimeSnapshot before, LifetimeSnapshot after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            Assert.Equal(0, Comparisons);
            Assert.Same(before.Owner, after.Owner);
            Assert.False(before.Owner.Disposed);
            Assert.Equal(1, before.Capture);
            Assert.Equal(2, after.Capture);
            Assert.Equal(new[] { "before", "after" }, before.Owner.Events);
            before.Owner.Events.Add("compare");
            Comparisons++;
            if (failure == "compare")
            {
                throw new IOException("lifetime-compare");
            }

            return Task.FromResult<IReadOnlyList<PipelineOutputChange>>([]);
        }

        public void Dispose() => Disposed = true;
    }
}

#pragma warning restore ASPIREPIPELINES005
