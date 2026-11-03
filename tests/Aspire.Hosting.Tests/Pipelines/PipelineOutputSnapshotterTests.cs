// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Hashing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public sealed class PipelineOutputSnapshotterTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestFileSystemService _fileSystem = new();

    [Fact]
    public async Task ComparisonIncludesAddedChangedRemovedAndUnchangedFiles()
    {
        var root = CreateDirectory();
        var added = Path.Combine(root, "added.txt");
        var changed = Write(root, "changed.txt", "before");
        var removed = Write(root, "removed.txt", "removed");
        var unchanged = Write(root, "unchanged.txt", "unchanged");
        var snapshotter = new PipelineOutputSnapshotter();
        var before = await snapshotter.CaptureAsync(root);
        Assert.Equal(new[] { changed, removed, unchanged }, before.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.False(File.Exists(added));
        Assert.Equal("before", File.ReadAllText(changed));

        Write(root, "added.txt", "added");
        Write(root, "changed.txt", "after!");
        File.Delete(removed);
        var after = await snapshotter.CaptureAsync(root);
        var comparison = DefaultPipelineOutputComparer.Compare(before, after, root);

        Assert.Collection(comparison,
            change => AssertChange(change, PipelineOutputChangeKind.Added, Path.GetRelativePath(root, added)),
            change => AssertChange(change, PipelineOutputChangeKind.Changed, Path.GetRelativePath(root, changed)),
            change => AssertChange(change, PipelineOutputChangeKind.Removed, Path.GetRelativePath(root, removed)));
    }

    [Fact]
    public async Task DefaultHashDetectsSameLengthChangesInLargeFiles()
    {
        var root = CreateDirectory();
        var path = Path.Combine(root, "large.bin");
        var bytes = new byte[1024 * 1024 + 17];
        bytes[0] = 1;
        File.WriteAllBytes(path, bytes);
        var snapshotter = new PipelineOutputSnapshotter();
        var before = await snapshotter.CaptureAsync(root);
        Assert.Equal(bytes.Length, new FileInfo(path).Length);
        Assert.Equal(0, bytes[^1]);

        bytes[^1] = 2;
        File.WriteAllBytes(path, bytes);
        var after = await snapshotter.CaptureAsync(root);

        Assert.Equal(bytes.Length, new FileInfo(path).Length);
        AssertChange(Assert.Single(DefaultPipelineOutputComparer.Compare(before, after, root)), PipelineOutputChangeKind.Changed, Path.GetRelativePath(root, path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(81920)]
    [InlineData(81921)]
    [InlineData(245777)]
    public async Task DefaultHashesMatchCustomStreamingHashesAcrossManyFiles(int length)
    {
        var root = CreateDirectory();
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var bytes = new byte[length];
        var random = new Random(42);
        for (var index = 0; index < 40; index++)
        {
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(index % 2 == 0 ? root : nested, $"{index:D2}.bin"), bytes);
        }
        var custom = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher(async (file, token) =>
        {
            using var stream = File.OpenRead(file.FullName);
            var hash = new XxHash3();
            await hash.AppendAsync(stream, token);
            return hash.GetCurrentHash();
        }));
        var expected = await custom.CaptureAsync(root);
        Assert.Equal(40, expected.Count);
        Assert.All(expected.Keys.Order(PipelineOutputRoot.PathComparer).ToArray(), path => Assert.Equal(length, new FileInfo(path).Length));

        var actual = await new PipelineOutputSnapshotter().CaptureAsync(root);

        Assert.Equal(expected.Keys.Order(PipelineOutputRoot.PathComparer).ToArray(), actual.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.All(expected.Keys.Order(PipelineOutputRoot.PathComparer).ToArray(), path =>
        {
            Assert.False(actual[path].IsLink);
            Assert.Equal(expected[path].Hash.ToArray(), actual[path].Hash.ToArray());
        });
    }

    [Fact]
    public async Task ConcurrentCapturesShareHasherDeduplicatePathsAndDisposeIndependentStreams()
    {
        var root = CreateDirectory();
        var first = Write(root, "first.txt", "a");
        var second = Write(root, "second.txt", "b");
        var calls = new ConcurrentBag<string>();
        var streams = new ConcurrentBag<Stream>();
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var hasher = new TestPipelineOutputFileHasher(async (entry, token) =>
        {
            using var stream = File.OpenRead(entry.FullName);
            streams.Add(stream);
            calls.Add(entry.FullName);
            if (Interlocked.Increment(ref started) == 2)
            {
                overlap.SetResult();
            }
            // Separate captures must overlap even when the runtime allows only one worker per capture.
            await overlap.Task.WaitAsync(token);
            var hash = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(hash, token));
            return hash;
        });
        var snapshotter = new PipelineOutputSnapshotter(hasher);
        Assert.False(overlap.Task.IsCompleted);
        Assert.Empty(calls);

        var firstCapture = snapshotter.CapturePathsAsync([new(first), new(first)], cts.Token);
        var secondCapture = snapshotter.CapturePathsAsync([new(second), new(second)], cts.Token);
        var failure = await Record.ExceptionAsync(async () => await Task.WhenAll(firstCapture, secondCapture));

        Assert.All(streams, stream => Assert.False(stream.CanRead));
        Assert.True(overlap.Task.IsCompletedSuccessfully, "Concurrent captures did not overlap before cancellation.");
        Assert.Null(failure);
        Assert.Equal(new[] { first, second }, calls.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.Equal(new[] { first }, (await firstCapture).Keys);
        Assert.Equal(new[] { second }, (await secondCapture).Keys);
        Assert.Equal(new byte[] { (byte)'a' }, (await firstCapture)[first].Hash.ToArray());
        Assert.Equal(new byte[] { (byte)'b' }, (await secondCapture)[second].Hash.ToArray());
        Assert.Equal(2, streams.Count);
        Assert.NotSame(streams.First(), streams.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomFailureOrCancellationDrainsRuntimeCapacityOwnedStreams(bool cancel)
    {
        var root = CreateDirectory();
        var first = Write(root, "first.txt", "a");
        var second = Write(root, "second.txt", "b");
        if (Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT") is "1")
        {
            Assert.Equal(1, Environment.ProcessorCount);
        }
        var workerCount = Math.Min(2, Environment.ProcessorCount);
        Assert.InRange(workerCount, 1, 2);
        var streams = new ConcurrentBag<Stream>();
        var workersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher(async (entry, token) =>
        {
            using var stream = File.OpenRead(entry.FullName);
            streams.Add(stream);
            if (Interlocked.Increment(ref started) == workerCount)
            {
                workersReady.SetResult();
            }
            await workersReady.Task.WaitAsync(token);
            if (entry.FullName == first)
            {
                if (cancel)
                {
                    cts.Cancel();
                }
                else
                {
                    throw new IOException("custom hash failure");
                }
            }
            // When capacity permits another worker, it retains its stream until loop cancellation.
            // A failed capture must await that worker's disposal rather than abandon it.
            await pending.Task.WaitAsync(token);
            return new byte[] { 42 };
        }));
        Assert.False(workersReady.Task.IsCompleted);
        Assert.Empty(streams);

        var failure = await Record.ExceptionAsync(() => snapshotter.CapturePathsAsync([new(first), new(second)], cts.Token));

        Assert.True(workersReady.Task.IsCompletedSuccessfully, "Runtime-capacity workers did not enter before cancellation.");
        Assert.Equal(workerCount, streams.Count);
        Assert.All(streams, stream => Assert.False(stream.CanRead));
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }
        else
        {
            Assert.Equal("custom hash failure", Assert.IsType<IOException>(failure).Message);
        }
        foreach (var path in new[] { first, second })
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(1, exclusive.Length);
        }
    }

    [Fact]
    public async Task DefaultCapturePropagatesFileOpenFailureAndClosesStreams()
    {
        var root = CreateDirectory();
        var paths = Enumerable.Range(0, 40).Select(index => Write(root, $"{index:D2}.txt", "content")).ToArray();
        var missing = Path.Combine(root, "missing.txt");
        Assert.False(File.Exists(missing));

        await Assert.ThrowsAsync<FileNotFoundException>(() => new PipelineOutputSnapshotter().CapturePathsAsync(
            paths.Prepend(missing).Select(path => new FileInfo(path)), CancellationToken.None));

        foreach (var path in paths)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(7, stream.Length);
        }
    }

    [Fact]
    public async Task MissingAndEmptyDirectoriesContributeNoFiles()
    {
        var root = CreateDirectory();
        var missing = Path.Combine(root, "missing");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.False(Directory.Exists(missing));

        var snapshotter = new PipelineOutputSnapshotter();
        var before = await snapshotter.CaptureAsync(missing);
        Directory.CreateDirectory(missing);
        var after = await snapshotter.CaptureAsync(missing);

        Assert.Empty(before.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.Empty(after.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.Empty(DefaultPipelineOutputComparer.Compare(before, after, root));
    }

    [Fact]
    public async Task PathCaseUsesPlatformPathComparison()
    {
        var parent = CreateDirectory();
        var lower = Path.Combine(parent, "output");
        var upper = Path.Combine(parent, "OUTPUT");
        var lowerFile = Write(lower, "artifact.txt", "content");
        var upperFile = Write(upper, "artifact.txt", "content");
        Assert.NotEqual(lowerFile, upperFile);
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((_, _) =>
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        }));

        var before = await snapshotter.CaptureAsync(lower);
        var after = await snapshotter.CaptureAsync(upper);
        var comparison = DefaultPipelineOutputComparer.Compare(before, after, parent);

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(comparison);
        }
        else
        {
            Assert.Collection(comparison,
                change => AssertChange(change, PipelineOutputChangeKind.Added, Path.GetRelativePath(parent, upperFile)),
                change => AssertChange(change, PipelineOutputChangeKind.Removed, Path.GetRelativePath(parent, lowerFile)));
        }
    }

    [Fact]
    public async Task HashBuffersAreCopiedAndOnlyHashEqualityDefinesChanges()
    {
        var root = CreateDirectory();
        var first = Write(root, "first.txt", "a");
        var second = Write(root, "second.txt", "b");
        var buffers = new ConcurrentBag<byte[]>();
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher(async (file, ct) =>
        {
            using var stream = File.OpenRead(file.FullName);
            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer, ct));
            buffers.Add(buffer);
            return buffer;
        }));
        var before = await snapshotter.CaptureAsync(root);
        Assert.Equal(new[] { first, second }, before.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.Equal(new byte[] { (byte)'a' }, before[first].Hash.ToArray());
        Assert.Equal(new byte[] { (byte)'b' }, before[second].Hash.ToArray());
        Assert.Equal(2, buffers.Count);
        foreach (var buffer in buffers)
        {
            buffer[0] = 255;
        }
        Assert.Equal(new byte[] { (byte)'a' }, before[first].Hash.ToArray());
        Assert.Equal(new byte[] { (byte)'b' }, before[second].Hash.ToArray());
        Assert.Equal("a", File.ReadAllText(first));

        Write(root, "first.txt", "c");
        var after = await snapshotter.CaptureAsync(root);

        AssertChange(Assert.Single(DefaultPipelineOutputComparer.Compare(before, after, root)), PipelineOutputChangeKind.Changed, Path.GetRelativePath(root, first));

        var constant = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 42 })));
        var shortCapture = await constant.CaptureAsync(root);
        var oldLength = new FileInfo(first).Length;
        Write(root, "first.txt", "different raw content and length");
        Assert.NotEqual(oldLength, new FileInfo(first).Length);
        var longCapture = await constant.CaptureAsync(root);

        Assert.False(shortCapture[first].Hash.Equals(longCapture[first].Hash));
        Assert.Equal(shortCapture[first].Hash.ToArray(), longCapture[first].Hash.ToArray());
        Assert.Empty(DefaultPipelineOutputComparer.Compare(shortCapture, longCapture, root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public async Task SynchronousSlicedHashesExcludeSentinelsAndCopyIndependentBuffers(int length)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var first = Write(workspace.Path, "first.txt", "alpha");
        var second = Write(workspace.Path, "second.txt", "beta");
        var buffers = new ConcurrentDictionary<string, byte[]>(PipelineOutputRoot.PathComparer);
        var expected = new[] { first, second }.ToDictionary(path => path,
            path => XxHash3.Hash(File.ReadAllBytes(path)).AsSpan(0, length).ToArray(), PipelineOutputRoot.PathComparer);
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((entry, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.IsType<FileInfo>(entry);
            var buffer = new byte[length + 2];
            buffer[0] = 165;
            buffer[^1] = 90;
            expected[entry.FullName].CopyTo(buffer, 1);
            Assert.True(buffers.TryAdd(entry.FullName, buffer));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(buffer.AsMemory(1, length));
        }));
        Assert.Empty(buffers);
        Assert.Equal("alpha", File.ReadAllText(first));
        Assert.Equal("beta", File.ReadAllText(second));

        var capture = await snapshotter.CaptureAsync(workspace.Path);

        Assert.Equal(new[] { first, second }, capture.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.Equal(2, buffers.Count);
        Assert.NotSame(buffers[first], buffers[second]);
        foreach (var path in new[] { first, second })
        {
            Assert.Equal(165, buffers[path][0]);
            Assert.Equal(90, buffers[path][^1]);
            Assert.Equal(expected[path], buffers[path].AsSpan(1, length).ToArray());
            Assert.Equal(expected[path], capture[path].Hash.ToArray());
            Array.Fill(buffers[path], (byte)255);
            Assert.Equal(expected[path], capture[path].Hash.ToArray());
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("cancel-return")]
    public async Task CallbackOwnsStreamDisposalAndEnginePropagatesEveryOutcome(string outcome)
    {
        var root = CreateDirectory();
        Write(root, "artifact.txt", "content");
        using var cts = new CancellationTokenSource();
        Stream? capturedStream = null;
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((file, ct) =>
        {
            using var stream = File.OpenRead(file.FullName);
            Assert.True(stream.CanRead);
            capturedStream = stream;
            if (outcome == "failure")
            {
                throw new InvalidOperationException("hash failure");
            }
            if (outcome is "cancel" or "cancel-return")
            {
                cts.Cancel();
                if (outcome == "cancel")
                {
                    ct.ThrowIfCancellationRequested();
                }
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        }));

        if (outcome == "failure")
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshotter.CaptureAsync(root, cts.Token));
            Assert.Equal("hash failure", exception.Message);
        }
        else if (outcome is "cancel" or "cancel-return")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshotter.CaptureAsync(root, cts.Token));
        }
        else
        {
            Assert.Single((await snapshotter.CaptureAsync(root, cts.Token)).Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        }

        Assert.NotNull(capturedStream);
        Assert.False(capturedStream.CanRead);
    }

    [Fact]
    public async Task CancellationBeforeCaptureDoesNotOpenFiles()
    {
        var root = CreateDirectory();
        Write(root, "artifact.txt", "content");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshotter.CaptureAsync(root, cts.Token));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".git/objects/content")]
    public async Task DirectCaptureDoesNotApplyGitSelection(string gitEntry)
    {
        var root = CreateDirectory();
        var gitPath = Write(root, gitEntry, "git content");
        var path = Write(root, "artifact.txt", "content");
        var snapshotter = new PipelineOutputSnapshotter();

        var capture = await snapshotter.CaptureAsync(root);

        Assert.Equal(new[] { gitPath, path }, capture.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
    }

    [Fact]
    public async Task FileRootIsRejectedRatherThanTreatedAsMissing()
    {
        var root = CreateDirectory();
        var path = Write(root, "artifact.txt", "content");
        Assert.True(File.Exists(path));

        await Assert.ThrowsAsync<IOException>(() => new PipelineOutputSnapshotter().CaptureAsync(path));
    }

    [Fact]
    public async Task DirectoryLinksAreCapturedWithoutFollowingTargetsOrCycles()
    {
        var parent = CreateDirectory();
        var target = Directory.CreateDirectory(Path.Combine(parent, "target")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(parent, "output")).FullName;
        var link = Path.Combine(root, "link");
        var external = Write(target, "artifact.txt", "old");
        Directory.CreateSymbolicLink(link, target);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        var snapshotter = new PipelineOutputSnapshotter();
        var before = await snapshotter.CaptureAsync(root);
        Assert.Equal(new[] { link }, before.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());

        File.WriteAllText(external, "new");

        var unchanged = await snapshotter.CaptureAsync(root);
        Assert.Empty(DefaultPipelineOutputComparer.Compare(before, unchanged, root));
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, root);

        var cyclic = await snapshotter.CaptureAsync(root);

        Assert.Equal(new[] { link }, cyclic.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        AssertChange(Assert.Single(DefaultPipelineOutputComparer.Compare(before, cyclic, root)), PipelineOutputChangeKind.Changed, Path.GetRelativePath(root, link));
        await Assert.ThrowsAsync<IOException>(() => snapshotter.CaptureAsync(link));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileLinksCompareTargetsAndEntryKindWithoutFollowingThem(bool broken)
    {
        var parent = CreateDirectory();
        var root = Directory.CreateDirectory(Path.Combine(parent, "output")).FullName;
        var target = Path.Combine(parent, "target.txt");
        if (!broken)
        {
            File.WriteAllText(target, "outside content");
        }
        var link = Path.Combine(root, "link");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Equal(!broken, File.Exists(target));
        var snapshotter = new PipelineOutputSnapshotter();
        var before = await snapshotter.CaptureAsync(root);
        Assert.Equal(new[] { link }, before.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());

        File.WriteAllText(target, "changed target contents");
        var unchanged = await snapshotter.CaptureAsync(root);
        Assert.Empty(DefaultPipelineOutputComparer.Compare(before, unchanged, root));
        File.Delete(link);
        File.WriteAllText(link, target);

        var regularFile = await snapshotter.CaptureAsync(root);

        AssertChange(Assert.Single(DefaultPipelineOutputComparer.Compare(before, regularFile, root)), PipelineOutputChangeKind.Changed, Path.GetRelativePath(root, link));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "100644", false)]
    [InlineData("100755", null, false)]
    [InlineData("100644", "100644", false)]
    [InlineData("100644", "100755", true)]
    public void ComparisonUsesGitModeOnlyWhenBothCapturesReportOne(string? beforeMode, string? afterMode, bool changed)
    {
        var root = CreateDirectory();
        var path = Path.Combine(root, "artifact.txt");
        var before = new Dictionary<string, PipelineOutputFileSnapshot> { [path] = new(false, new byte[] { 42 }, beforeMode) };
        var after = new Dictionary<string, PipelineOutputFileSnapshot> { [path] = new(false, new byte[] { 42 }, afterMode) };
        Assert.Equal(before[path].Hash.ToArray(), after[path].Hash.ToArray());

        var changes = DefaultPipelineOutputComparer.Compare(before, after, root);

        if (changed)
        {
            AssertChange(Assert.Single(changes), PipelineOutputChangeKind.Changed, "artifact.txt");
        }
        else
        {
            Assert.Empty(changes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomCallbackReceivesTypedSelectedLeavesWithoutFollowingDirectoryLinks(bool cyclic)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("output").FullName;
        var outside = workspace.CreateDirectory("outside").FullName;
        var external = Write(outside, "external.txt", "outside contents");
        var regular = Write(root, "nested/artifact.txt", "selected contents");
        var directoryLink = Path.Combine(root, "directory-link");
        var directoryTarget = cyclic ? root : outside;
        Directory.CreateSymbolicLink(directoryLink, directoryTarget);
        var fileLink = Path.Combine(root, "file-link");
        var missingTarget = Path.Combine(workspace.Path, "missing.txt");
        File.CreateSymbolicLink(fileLink, missingTarget);
        Assert.Equal(directoryTarget, new DirectoryInfo(directoryLink).LinkTarget);
        Assert.True((File.GetAttributes(directoryLink) & FileAttributes.Directory) != 0);
        Assert.True((File.GetAttributes(directoryLink) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(missingTarget, new FileInfo(fileLink).LinkTarget);
        Assert.False(File.Exists(missingTarget));
        Assert.Equal("outside contents", File.ReadAllText(external));
        Assert.Equal("selected contents", File.ReadAllText(regular));
        var calls = new ConcurrentBag<string>();
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((entry, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add(entry.FullName);
            if (entry.FullName == directoryLink)
            {
                var directory = Assert.IsType<DirectoryInfo>(entry);
                Assert.Equal(directoryTarget, directory.LinkTarget);
                // Hash the link itself, without opening or enumerating its target.
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(directory.LinkTarget!));
            }
            var file = Assert.IsType<FileInfo>(entry);
            Assert.Contains(file.FullName, new[] { regular, fileLink });
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(file.LinkTarget is { } target
                ? System.Text.Encoding.UTF8.GetBytes(target)
                : File.ReadAllBytes(file.FullName));
        }));
        Assert.Empty(calls);

        var capture = await snapshotter.CaptureAsync(root);

        var selected = new[] { directoryLink, fileLink, regular }.Order(PipelineOutputRoot.PathComparer).ToArray();
        Assert.Equal(selected, calls.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.Equal(selected, capture.Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Assert.True(capture[directoryLink].IsLink);
        Assert.True(capture[fileLink].IsLink);
        Assert.False(capture[regular].IsLink);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(directoryTarget), capture[directoryLink].Hash.ToArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(missingTarget), capture[fileLink].Hash.ToArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("selected contents"), capture[regular].Hash.ToArray());
        Assert.False(File.Exists(missingTarget));
        Assert.Equal("outside contents", File.ReadAllText(external));
    }

    [Fact]
    public async Task CustomCallbackReceivesBrokenLinkMetadataWithoutEngineOpeningItsTarget()
    {
        var root = CreateDirectory();
        var target = Path.Combine(root, "missing-target.txt");
        var link = Path.Combine(root, "link");
        File.CreateSymbolicLink(link, target);
        Assert.False(File.Exists(target));
        var calls = 0;
        var snapshotter = new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher((file, token) =>
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            Assert.IsType<FileInfo>(file);
            Assert.Equal(link, file.FullName);
            Assert.Equal(target, file.LinkTarget);
            Assert.True((file.Attributes & FileAttributes.ReparsePoint) != 0);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(file.LinkTarget!));
        }));

        var capture = await snapshotter.CaptureAsync(root);

        Assert.Equal(1, calls);
        Assert.True(capture[link].IsLink);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(target), capture[link].Hash.ToArray());
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("nested/..")]
    [InlineData("nested/../")]
    public void NormalizeDirectoryPaths(string relativePath)
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Path.IsPathFullyQualified(path));

        Assert.Equal(directory, PipelineOutputRoot.NormalizeDirectoryPath(path));
    }

    [Fact]
    public void RelativeDirectoryPathsResolveAgainstCurrentDirectory()
    {
        Assert.False(Path.IsPathFullyQualified("."));

        Assert.Equal(Directory.GetCurrentDirectory(), PipelineOutputRoot.NormalizeDirectoryPath("."));
    }

    [Fact]
    public void NormalizationPreservesFilesystemRoots()
    {
        var root = Path.GetPathRoot(CreateDirectory())!;
        Assert.True(Path.EndsInDirectorySeparator(root));

        Assert.Equal(root, PipelineOutputRoot.NormalizeDirectoryPath(root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void NormalizationRejectsBlankDirectories(string? path)
    {
        Assert.True(string.IsNullOrWhiteSpace(path));

        Assert.ThrowsAny<ArgumentException>(() => PipelineOutputRoot.NormalizeDirectoryPath(path!));
    }

    public void Dispose() => _fileSystem.Dispose();

    private string CreateDirectory() => _fileSystem.TempDirectory.CreateTempSubdirectory().Path;

    private static string Write(string directory, string relativePath, string content)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void AssertChange(PipelineOutputChange change, PipelineOutputChangeKind kind, string path)
    {
        Assert.Equal(kind, change.Kind);
        Assert.Equal(path, change.Path);
    }
}

#pragma warning restore ASPIREPIPELINES005
