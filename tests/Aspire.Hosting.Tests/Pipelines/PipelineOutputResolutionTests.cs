// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

public class PipelineOutputResolutionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EachCaptureResolvesItsProviderAndComparisonUsesBothSnapshots(bool singleton, bool sequential)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "artifact.txt");
        await File.WriteAllTextAsync(artifact, "before", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotters = new List<ContentSnapshotter>();
        var comparer = new ContentComparer();
        ContentSnapshotter Create(IServiceProvider services)
        {
            var snapshotter = new ContentSnapshotter();
            snapshotters.Add(snapshotter);
            return snapshotter;
        }
        if (singleton)
        {
            builder.Services.AddSingleton<IPipelineOutputSnapshotter<ContentSnapshot>>(Create);
        }
        else
        {
            builder.Services.AddTransient<IPipelineOutputSnapshotter<ContentSnapshot>>(Create);
        }
        builder.Services.AddSingleton<IPipelineOutputComparer<ContentSnapshot>>(comparer);
        builder.Services.AddSingleton<PipelineOutputSnapshotter>(_ => throw new InvalidOperationException("Default capture must not resolve."));
        builder.WithOutputDiffing<ContentSnapshot, ContentSnapshotter, ContentComparer>();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, _ =>
            File.WriteAllTextAsync(artifact, "after", TestContext.Current.CancellationToken));
        using var app = builder.Build();
        var context = NewContext(app);
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        Assert.Empty(snapshotters);
        Assert.Empty(comparer.Comparisons);
        Assert.Equal("before", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => sequential
            ? pipeline.ExecuteStepSequentiallyAsync("publish-and-diff", context)
            : pipeline.ExecuteAsync(context));

        Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: artifact.txt", failure.Message);
        var comparison = Assert.Single(comparer.Comparisons);
        Assert.Equal(new(workspace.Path, "before"), comparison.Before);
        Assert.Equal(new(workspace.Path, "after"), comparison.After);
        Assert.Equal("after", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.Equal(singleton ? 1 : 2, snapshotters.Count);
        Assert.All(snapshotters, snapshotter => Assert.Equal(singleton ? 2 : 1, snapshotter.Captures.Count));
        Assert.All(snapshotters, snapshotter => Assert.False(snapshotter.Disposed));
        app.Dispose();
        Assert.All(snapshotters, snapshotter => Assert.True(snapshotter.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultValueSnapshotIsACapturedBaseline(bool sequential)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotter = new ValueSnapshotter();
        var comparer = new ValueComparer();
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<int>>(snapshotter);
        builder.Services.AddSingleton<IPipelineOutputComparer<int>>(comparer);
        builder.WithOutputDiffing<int, ValueSnapshotter, ValueComparer>();
        using var app = builder.Build();
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = NewContext(app);
        Assert.Equal(0, snapshotter.Captures);
        Assert.Equal(0, comparer.Comparisons);

        await Run();
        Assert.Equal(2, snapshotter.Captures);
        Assert.Equal(1, comparer.Comparisons);
        await Run();
        Assert.Equal(4, snapshotter.Captures);
        Assert.Equal(2, comparer.Comparisons);

        Task Run() => sequential
            ? pipeline.ExecuteStepSequentiallyAsync("publish-and-diff", context)
            : pipeline.ExecuteAsync(context);
    }

    [Theory]
    [InlineData(false, PipelineOutputDiffMode.Auto)]
    [InlineData(true, PipelineOutputDiffMode.Auto)]
    [InlineData(false, PipelineOutputDiffMode.FileSystem)]
    [InlineData(true, PipelineOutputDiffMode.FileSystem)]
    [InlineData(true, PipelineOutputDiffMode.Git)]
    public async Task WorktreeAvailabilityChangesBetweenCaptures(bool initiallyGit, PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("output").FullName;
        var artifact = Path.Combine(root, "artifact.txt");
        await File.WriteAllTextAsync(artifact, "unchanged\n", TestContext.Current.CancellationToken);
        if (initiallyGit)
        {
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
        }
        var administration = Path.Combine(workspace.Path, ".git");
        Assert.Equal(initiallyGit, Directory.Exists(administration));
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish,
            outputPath: root, step: "publish-and-diff");
        builder.WithOutputDiffing();
        builder.Services.Configure<PipelineOutputDiffOptions>(options => options.Mode = mode);
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            Assert.Equal(initiallyGit, Directory.Exists(administration));
            if (initiallyGit)
            {
                Directory.Move(administration, Path.Combine(workspace.Path, "saved-git"));
            }
            else
            {
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
            }
            Assert.Equal(!initiallyGit, Directory.Exists(administration));
        });
        using var app = builder.Build();

        if (mode == PipelineOutputDiffMode.FileSystem)
        {
            await ExecuteAsync(app);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            if (mode == PipelineOutputDiffMode.Auto)
            {
                // Git object IDs and raw-byte hashes differ even though the file does not.
                Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: artifact.txt", failure.Message);
            }
            else
            {
                Assert.Contains("Git is required", failure.Message);
            }
        }
        Assert.Equal("unchanged\n", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.Equal(!initiallyGit, Directory.Exists(administration));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureErrorsAndCancellationDoNotFallBack(bool cancel)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new PipelineOutputTestProcessRunner(args =>
        {
            if (args.Contains("rev-parse"))
            {
                return new(0, "true\n\n", "");
            }
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return new(1, "", "capture-failure");
        });
        var snapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions()));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Empty(runner.Calls);

        if (cancel)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                snapshotter.CaptureAsync(workspace.Path, cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
                snapshotter.CaptureAsync(workspace.Path, cancellation.Token));
            Assert.Contains("capture-failure", failure.Message);
        }
        Assert.Equal(2, runner.Calls.Count);
    }

    private static PipelineContext NewContext(DistributedApplication app) => new(
        app.Services.GetRequiredService<DistributedApplicationModel>(),
        app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
        NullLogger.Instance, TestContext.Current.CancellationToken);

    private static Task ExecuteAsync(DistributedApplication app) =>
        app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(NewContext(app));

    private sealed class ValueSnapshotter : IPipelineOutputSnapshotter<int>
    {
        public int Captures { get; private set; }

        public Task<int> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Captures++;
            return Task.FromResult(0);
        }
    }

    private sealed class ValueComparer : IPipelineOutputComparer<int>
    {
        public int Comparisons { get; private set; }

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(int before, int after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(0, before);
            Assert.Equal(0, after);
            Comparisons++;
            return Task.FromResult<IReadOnlyList<PipelineOutputChange>>([]);
        }
    }

    private readonly record struct ContentSnapshot(string Directory, string Content);

    private sealed class ContentSnapshotter : IPipelineOutputSnapshotter<ContentSnapshot>, IDisposable
    {
        public List<ContentSnapshot> Captures { get; } = [];
        public bool Disposed { get; private set; }

        public async Task<ContentSnapshot> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            Assert.False(Disposed);
            var snapshot = new ContentSnapshot(outputDirectory,
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "artifact.txt"), ct));
            Captures.Add(snapshot);
            return snapshot;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ContentComparer : IPipelineOutputComparer<ContentSnapshot>
    {
        public List<(ContentSnapshot Before, ContentSnapshot After)> Comparisons { get; } = [];

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(ContentSnapshot before, ContentSnapshot after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Comparisons.Add((before, after));
            return Task.FromResult<IReadOnlyList<PipelineOutputChange>>(
                before.Content == after.Content ? [] : [new(PipelineOutputChangeKind.Changed, "artifact.txt")]);
        }
    }
}

#pragma warning restore ASPIREPIPELINES005
