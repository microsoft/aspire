// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public sealed class PipelineOutputComparisonTests : IDisposable
{
    private readonly TestFileSystemService _fileSystem = new();

    [Fact]
    public async Task RepeatedDefaultComparisonUsesCapturedDataAfterLiveOutputChangesOrDisappears()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var added = Path.Combine(root, "added.txt");
        var changed = Path.Combine(root, "changed.txt");
        var removed = Path.Combine(root, "removed.txt");
        var unchanged = Path.Combine(root, "unchanged.txt");
        File.WriteAllText(changed, "before");
        File.WriteAllText(removed, "removed");
        File.WriteAllText(unchanged, "unchanged");
        var snapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        var before = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { changed, removed, unchanged }, before.Files.Keys.Order(PipelineOutputRoot.PathComparer));
        Assert.False(File.Exists(added));

        File.WriteAllText(added, "added");
        File.WriteAllText(changed, "after!");
        File.Delete(removed);
        var after = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { added, changed, unchanged }, after.Files.Keys.Order(PipelineOutputRoot.PathComparer));
        File.WriteAllText(changed, "third version must not be read");
        File.WriteAllText(unchanged, "also changed after capture");
        var comparer = new DefaultPipelineOutputComparer();

        var changes = await comparer.CompareAsync(before, after, TestContext.Current.CancellationToken);

        Assert.Collection(changes,
            change => { Assert.Equal(PipelineOutputChangeKind.Added, change.Kind); Assert.Equal("added.txt", change.Path); },
            change => { Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind); Assert.Equal("changed.txt", change.Path); },
            change => { Assert.Equal(PipelineOutputChangeKind.Removed, change.Kind); Assert.Equal("removed.txt", change.Path); });
        Directory.Delete(root, recursive: true);
        Assert.False(Directory.Exists(root));
        var repeated = await comparer.CompareAsync(before, after, TestContext.Current.CancellationToken);
        Assert.Equal(changes.Select(change => (change.Kind, change.Path)), repeated.Select(change => (change.Kind, change.Path)));
        Assert.Empty(await comparer.CompareAsync(after, after, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedCaptureCanBeRetriedWithCurrentOutput()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var path = Path.Combine(root, "artifact.txt");
        File.WriteAllText(path, "before");
        var calls = 0;
        var snapshotter = new DefaultPipelineOutputSnapshotter(new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((file, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("capture failure");
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(File.ReadAllBytes(file.FullName));
        })), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));

        var exception = await Assert.ThrowsAsync<IOException>(() => snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken));

        Assert.Equal("capture failure", exception.Message);
        Assert.Equal(1, calls);
        File.WriteAllText(path, "after!");
        var capture = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(path, Assert.Single(capture.Files).Key);
        Assert.Equal(File.ReadAllBytes(path), capture.Files[path].Hash.ToArray());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task IndependentDefaultSnapshottersCanShareLowLevelSnapshotter()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var path = Path.Combine(root, "artifact.txt");
        File.WriteAllText(path, "first");
        var snapshotter = new PipelineOutputSnapshotter();
        var first = new DefaultPipelineOutputSnapshotter(snapshotter, new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        var second = new DefaultPipelineOutputSnapshotter(snapshotter, new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        var firstBefore = await first.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal("first", File.ReadAllText(path));
        File.WriteAllText(path, "second");
        var secondBefore = await second.CaptureAsync(root, TestContext.Current.CancellationToken);
        var secondAfter = await second.CaptureAsync(root, TestContext.Current.CancellationToken);
        var firstAfter = await first.CaptureAsync(root, TestContext.Current.CancellationToken);
        var comparer = new DefaultPipelineOutputComparer();

        Assert.Empty(await comparer.CompareAsync(secondBefore, secondAfter, TestContext.Current.CancellationToken));
        var change = Assert.Single(await comparer.CompareAsync(firstBefore, firstAfter, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("artifact.txt", change.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultCaptureObservesCancellationBeforeOpeningFilesOrReturningMissingRoot(bool rootExists)
    {
        var parent = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var root = rootExists ? parent : Path.Combine(parent, "missing");
        if (rootExists)
        {
            File.WriteAllText(Path.Combine(root, "artifact.txt"), "content");
        }
        Assert.Equal(rootExists, Directory.Exists(root));
        var calls = 0;
        var snapshotter = new DefaultPipelineOutputSnapshotter(new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 42 });
        })), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(cts.IsCancellationRequested);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshotter.CaptureAsync(root, cts.Token));

        Assert.Equal(0, calls);
        var capture = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(rootExists ? 1 : 0, capture.Files.Count);
        Assert.Equal(rootExists ? 1 : 0, calls);
    }

    [Fact]
    public async Task ComparisonObservesCancellationWithoutConsumingCaptures()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var path = Path.Combine(root, "artifact.txt");
        File.WriteAllText(path, "before");
        var snapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        var before = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "after!");
        var after = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        IPipelineOutputComparer<PipelineOutputSnapshot> comparer = new DefaultPipelineOutputComparer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(cts.IsCancellationRequested);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comparer.CompareAsync(before, after, cts.Token));

        var change = Assert.Single(await comparer.CompareAsync(before, after, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("artifact.txt", change.Path);
    }

    [Fact]
    public async Task ComparisonReportsPathsRelativeToAfterSnapshotDirectory()
    {
        var parent = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var root = Directory.CreateDirectory(Path.Combine(parent, "nested")).FullName;
        var path = Path.Combine(root, "artifact.txt");
        File.WriteAllText(path, "before");
        var snapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        var before = await snapshotter.CaptureAsync(parent, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "after!");
        var after = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(parent, before.OutputDirectory);
        Assert.Equal(root, after.OutputDirectory);
        Assert.NotEqual(before.OutputDirectory, after.OutputDirectory);
        var comparer = new DefaultPipelineOutputComparer();

        var change = Assert.Single(await comparer.CompareAsync(before, after, TestContext.Current.CancellationToken));

        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("artifact.txt", change.Path);
        var reverse = Assert.Single(await comparer.CompareAsync(after, before, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, reverse.Kind);
        Assert.Equal(Path.Combine("nested", "artifact.txt"), reverse.Path);
    }

    public void Dispose() => _fileSystem.Dispose();
}

#pragma warning restore ASPIREPIPELINES005
