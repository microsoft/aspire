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
        var capture = "before";
        var writerCalls = 0;
        builder.Services.AddTransient<IPipelineOutputSnapshotter<LifetimeSnapshot>>(_ =>
        {
            var snapshotter = new LifetimeSnapshotter(capture, firstRun ? failure : "none");
            capture = "after";
            snapshotters.Add(snapshotter);
            return snapshotter;
        });
        builder.Services.AddTransient<IPipelineOutputComparer<LifetimeSnapshot>>(_ =>
        {
            var comparer = new LifetimeComparer(firstRun ? failure : "none", snapshotters[^2], snapshotters[^1]);
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
        var firstSnapshotterCount = failure is "before" or "cancel" or "writer" ? 1 : 2;
        var firstComparerCount = failure is "none" or "compare" ? 1 : 0;
        Assert.Equal(firstSnapshotterCount, snapshotters.Count);
        Assert.Equal(new[] { "before" }, snapshotters[0].Events);
        var expectedAfterEvents = firstComparerCount == 0 ? new[] { "after" } : new[] { "after", "compare" };
        if (firstSnapshotterCount == 2)
        {
            Assert.Equal(expectedAfterEvents, snapshotters[1].Events);
        }
        Assert.Equal(firstComparerCount, comparers.Count);
        Assert.All(comparers, comparer => Assert.Equal(1, comparer.Comparisons));
        Assert.Equal(failure is "before" or "cancel" ? 0 : 1, writerCalls);
        Assert.All(snapshotters, snapshotter => Assert.False(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.False(comparer.Disposed));
        firstRun = false;
        capture = "before";

        // Ordinary publish must not construct either provider or reuse a completed capture.
        await Run(pipeline, NewContext(), WellKnownPipelineSteps.Publish);
        Assert.Equal(firstSnapshotterCount, snapshotters.Count);
        Assert.Equal(firstComparerCount, comparers.Count);

        await Run(pipeline, context, "publish-and-diff");
        Assert.Equal(firstSnapshotterCount + 2, snapshotters.Count);
        Assert.Equal(snapshotters.Count, snapshotters.Distinct().Count());
        Assert.Equal(new[] { "before" }, snapshotters[0].Events);
        if (firstSnapshotterCount == 2)
        {
            Assert.Equal(expectedAfterEvents, snapshotters[1].Events);
        }
        Assert.Equal(new[] { "before" }, snapshotters[^2].Events);
        Assert.Equal(new[] { "after", "compare" }, snapshotters[^1].Events);
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
            var snapshotter = new LifetimeSnapshotter(snapshotters.Count % 2 == 0 ? "before" : "after", "none");
            snapshotters.Add(snapshotter);
            return snapshotter;
        });
        builder.Services.AddTransient<IPipelineOutputComparer<LifetimeSnapshot>>(_ =>
        {
            var comparer = new LifetimeComparer("none", snapshotters[^2], snapshotters[^1]);
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
        Assert.Equal(2, snapshotters.Count);
        var firstComparer = Assert.Single(comparers);
        Assert.Equal(new[] { "before" }, snapshotters[0].Events);
        Assert.Equal(new[] { "after", "compare" }, snapshotters[1].Events);
        await pipeline.Clone().ExecuteAsync(context);

        Assert.Equal(4, snapshotters.Count);
        Assert.Equal(4, snapshotters.Distinct().Count());
        Assert.Equal(2, comparers.Count);
        Assert.NotSame(firstComparer, comparers[1]);
        Assert.Equal(new[] { "before" }, snapshotters[0].Events);
        Assert.Equal(new[] { "after", "compare" }, snapshotters[1].Events);
        Assert.Equal(new[] { "before" }, snapshotters[2].Events);
        Assert.Equal(new[] { "after", "compare" }, snapshotters[3].Events);
        Assert.All(comparers, comparer => Assert.Equal(1, comparer.Comparisons));
        Assert.All(snapshotters, snapshotter => Assert.False(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.False(comparer.Disposed));
        app.Dispose();
        Assert.All(snapshotters, snapshotter => Assert.True(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.True(comparer.Disposed));
    }

    private sealed record LifetimeSnapshot(LifetimeSnapshotter Owner);

    private sealed class LifetimeSnapshotter(string capture, string failure) : IPipelineOutputSnapshotter<LifetimeSnapshot>, IDisposable
    {
        public List<string> Events { get; } = [];
        public bool Disposed { get; private set; }

        public Task<LifetimeSnapshot> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            Assert.NotEmpty(outputDirectory);
            Assert.Empty(Events);
            Events.Add(capture);
            if (failure == capture)
            {
                throw new IOException("lifetime-" + capture);
            }
            if (capture == "before" && failure == "cancel")
            {
                throw new OperationCanceledException("lifetime-cancel", new CancellationToken(canceled: true));
            }

            return Task.FromResult(new LifetimeSnapshot(this));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class LifetimeComparer(string failure, LifetimeSnapshotter beforeOwner, LifetimeSnapshotter afterOwner) : IPipelineOutputComparer<LifetimeSnapshot>, IDisposable
    {
        public int Comparisons { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(LifetimeSnapshot before, LifetimeSnapshot after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            Assert.Equal(0, Comparisons);
            Assert.Same(beforeOwner, before.Owner);
            Assert.Same(afterOwner, after.Owner);
            Assert.NotSame(before.Owner, after.Owner);
            Assert.False(before.Owner.Disposed);
            Assert.False(after.Owner.Disposed);
            Assert.Equal(new[] { "before" }, before.Owner.Events);
            Assert.Equal(new[] { "after" }, after.Owner.Events);
            after.Owner.Events.Add("compare");
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
