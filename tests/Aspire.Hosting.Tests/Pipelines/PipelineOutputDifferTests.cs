// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public sealed class PipelineOutputDifferTests : IDisposable
{
    private readonly TestFileSystemService _fileSystem = new();

    [Fact]
    public async Task NativeDifferRequiresBeforeThenAfterBeforeComparison()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var differ = new SnapshotPipelineOutputDiffer(new PipelineOutputSnapshotter(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CaptureAfterAsync(root, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CompareAsync(default));
        await differ.CaptureBeforeAsync(root, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CaptureBeforeAsync(root, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CompareAsync(default));
        await differ.CaptureAfterAsync(root, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CaptureAfterAsync(root, default));

        Assert.Empty(await differ.CompareAsync(default));
    }

    [Fact]
    public async Task FailedBaselineDoesNotEnableAfterCaptureOrComparison()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        File.WriteAllText(Path.Combine(root, "artifact.txt"), "content");
        var differ = new SnapshotPipelineOutputDiffer(new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((_, _) =>
            throw new IOException("capture failure"))), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));

        var exception = await Assert.ThrowsAsync<IOException>(() => differ.CaptureBeforeAsync(root, default));

        Assert.Equal("capture failure", exception.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CaptureAfterAsync(root, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => differ.CompareAsync(default));
    }

    [Fact]
    public async Task IndependentDiffersCanShareStatelessSnapshotter()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var path = Path.Combine(root, "artifact.txt");
        File.WriteAllText(path, "first");
        var snapshotter = new PipelineOutputSnapshotter();
        var first = new SnapshotPipelineOutputDiffer(snapshotter, new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var second = new SnapshotPipelineOutputDiffer(snapshotter, new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        await first.CaptureBeforeAsync(root, default);
        Assert.Equal("first", File.ReadAllText(path));
        File.WriteAllText(path, "second");
        await second.CaptureBeforeAsync(root, default);
        await second.CaptureAfterAsync(root, default);
        await first.CaptureAfterAsync(root, default);

        Assert.Empty(await second.CompareAsync(default));
        var change = Assert.Single(await first.CompareAsync(default));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("artifact.txt", change.Path);
    }

    [Fact]
    public async Task NativeDifferObservesCancellationAtEveryPhase()
    {
        var root = _fileSystem.TempDirectory.CreateTempSubdirectory().Path;
        var differ = new SnapshotPipelineOutputDiffer(new PipelineOutputSnapshotter(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(cts.IsCancellationRequested);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => differ.CaptureBeforeAsync(root, cts.Token));
        await differ.CaptureBeforeAsync(root, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => differ.CaptureAfterAsync(root, cts.Token));
        await differ.CaptureAfterAsync(root, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => differ.CompareAsync(cts.Token));

        Assert.Empty(await differ.CompareAsync(default));
    }

    public void Dispose() => _fileSystem.Dispose();
}

#pragma warning restore ASPIREPIPELINES005
