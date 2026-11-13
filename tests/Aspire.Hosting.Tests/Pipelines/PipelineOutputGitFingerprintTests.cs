// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

public sealed class PipelineOutputGitFingerprintTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("text eol=lf", "line\n", "line\r\n", false, true)]
    [InlineData("text eol=lf", "line\r\n", "line\n", false, true)]
    [InlineData("text eol=crlf", "line\n", "line\r\n", false, true)]
    [InlineData("text eol=crlf", "line\r\n", "line\n", false, true)]
    [InlineData("text eol=lf", "line\n", "line\r\n", false, false)]
    [InlineData("text eol=lf", "line\r\n", "line\n", false, false)]
    [InlineData("binary", "line\n", "line\r\n", true, true)]
    [InlineData("-text", "line\r\n", "line\n", true, true)]
    [InlineData("text eol=lf", "line\n", "changed\r\n", true, true)]
    [InlineData("text eol=lf", "line\n", "line", true, true)]
    [InlineData("text eol=lf", "line", "line\r\n", true, true)]
    [InlineData("text eol=lf", "line\r\n", "line\r\n", false, true)]
    public async Task GitModeUsesCanonicalContentAndFilesystemModeUsesRawContent(
        string attributes, string before, string after, bool changed, bool useGit)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "*.txt " + attributes + "\n");
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        File.WriteAllText(path, before);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", ".gitattributes", "output/model.txt");
        var index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        var objects = ReadObjects(workspace.Path);
        var beforeOid = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "hash-object", "--", "output/model.txt");
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = useGit ? PipelineOutputDiffMode.Git : PipelineOutputDiffMode.FileSystem }));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        File.WriteAllText(path, after);
        var afterOid = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "hash-object", "--", "output/model.txt");
        Assert.Equal(changed, beforeOid != afterOid);
        var expectedChanged = useGit ? changed : before != after;
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);

        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
        if (expectedChanged)
        {
            var change = Assert.Single(changes);
            Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
            Assert.Equal(Path.GetRelativePath(root, path), change.Path);
        }
        else
        {
            Assert.Empty(changes);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanFilterEquivalentResidualFilesHaveEqualNativeFingerprints(bool tracked)
    {
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-filter-"));
        await InitializeAsync(workspace.Path, "sha1");
        // A directory-scoped pattern exercises attribute path lookup rather than suffix matching.
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "output*/**/*.txt filter=stripspace -text\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.stripspace.clean", "git stripspace");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.stripspace.required", "true");
        var root = workspace.CreateDirectory("output 雪 space").FullName;
        var path = Path.Combine(root, "model café name.txt");
        var gitPath = "output 雪 space/model café name.txt";
        if (tracked)
        {
            File.WriteAllText(path, "different indexed content\n");
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", gitPath);
        }

        // stripspace removes leading/trailing blank lines and trailing whitespace, so these
        // different physical inputs represent the same Git blob without changing the index.
        File.WriteAllText(path, "\ncanonical content   \n\n");
        var beforeBytes = File.ReadAllBytes(path);
        await AssertResidualAsync();
        var beforeOid = (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "hash-object", "--", gitPath)).TrimEnd('\r', '\n');
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var before = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(path, Assert.Single(before.Files).Key);

        File.WriteAllText(path, "canonical content\t \n");
        Assert.NotEqual(beforeBytes, File.ReadAllBytes(path));
        await AssertResidualAsync();
        var afterOid = (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "hash-object", "--", gitPath)).TrimEnd('\r', '\n');
        Assert.Equal(40, beforeOid.Length);
        Assert.Equal(beforeOid, afterOid);
        var after = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(path, Assert.Single(after.Files).Key);
        output.WriteLine($"tracked={tracked}; canonical before={beforeOid}; canonical after={afterOid}; captured before={Encoding.ASCII.GetString(before.Files[path].Hash.Span)}; captured after={Encoding.ASCII.GetString(after.Files[path].Hash.Span)}");

        var changes = await new DefaultPipelineOutputComparer().CompareAsync(before, after, TestContext.Current.CancellationToken);
        Assert.Multiple(
            () => Assert.Equal(beforeOid, Encoding.ASCII.GetString(before.Files[path].Hash.Span)),
            () => Assert.Equal(afterOid, Encoding.ASCII.GetString(after.Files[path].Hash.Span)),
            () => Assert.Empty(changes));

        async Task AssertResidualAsync()
        {
            var indexed = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--cached", "--full-name", "-z", "--", gitPath);
            var untracked = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--others", "--exclude-standard", "--full-name", "-z", "--", gitPath);
            Assert.Equal(tracked ? gitPath + "\0" : "", indexed);
            Assert.Equal(tracked ? "" : gitPath + "\0", untracked);
            var status = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "status",
                "--porcelain=v2", "-z", "--untracked-files=no", "--no-renames");
            if (tracked)
            {
                // Porcelain v2: "1 XY sub mH mI mW hH hI path\0". The path contains spaces;
                // split only the eight header fields. Y='M' rules out clean-index hash reuse.
                var fields = Assert.Single(status.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Split(' ', 9);
                Assert.Equal('M', fields[1][1]);
                Assert.Equal(gitPath, fields[8]);
            }
            else
            {
                Assert.Empty(status);
            }
        }
    }

    [Theory]
    [InlineData("index\n", "index\n", "index\n", null)]
    [InlineData("index\n", "dirty\n", "dirty\n", null)]
    [InlineData("index\n", "dirty\n", "index\n", PipelineOutputChangeKind.Changed)]
    [InlineData("index\n", "index\n", "changed\n", PipelineOutputChangeKind.Changed)]
    [InlineData(null, "untracked\n", "untracked\n", null)]
    [InlineData(null, "untracked\n", "changed\n", PipelineOutputChangeKind.Changed)]
    [InlineData("index\n", null, "restored\n", PipelineOutputChangeKind.Added)]
    [InlineData("index\n", "index\n", null, PipelineOutputChangeKind.Removed)]
    public async Task GitComparisonUsesCurrentWorkingTreeRatherThanIndexOrHead(
        string? indexed, string? before, string? after, PipelineOutputChangeKind? expected)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        if (indexed is not null)
        {
            File.WriteAllText(path, indexed);
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        }
        SetContent(before);
        Assert.Equal(before is not null, File.Exists(path));
        var indexPath = Path.Combine(workspace.Path, ".git", "index");
        var index = File.Exists(indexPath) ? File.ReadAllBytes(indexPath) : null;
        var objects = ReadObjects(workspace.Path);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        SetContent(after);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);

        Assert.Equal(index is not null, File.Exists(indexPath));
        if (index is not null)
        {
            Assert.Equal(index, File.ReadAllBytes(indexPath));
        }
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
        if (expected is { } kind)
        {
            var change = Assert.Single(changes);
            Assert.Equal(kind, change.Kind);
            Assert.Equal(Path.GetRelativePath(root, path), change.Path);
        }
        else
        {
            Assert.Empty(changes);
        }

        void SetContent(string? content)
        {
            if (content is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, content);
            }
        }
    }

    [Fact]
    public async Task GitModeChangesMatterWhenFileModeConfigurationTrustsTheIndex()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.filemode", "false");
        var root = workspace.CreateDirectory("output").FullName;
        File.WriteAllText(Path.Combine(root, "model.txt"), "same content\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        var beforeEntry = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/model.txt");
        Assert.StartsWith("100644 ", beforeEntry);
        var indexPath = Path.Combine(workspace.Path, ".git", "index");
        var beforeIndex = File.ReadAllBytes(indexPath);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(beforeIndex, File.ReadAllBytes(indexPath));

        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", "--chmod=+x", "--", "output/model.txt");
        var afterEntry = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/model.txt");
        Assert.StartsWith("100755 ", afterEntry);
        Assert.Equal(beforeEntry.Split(' ')[1], afterEntry.Split(' ')[1]);
        var afterIndex = File.ReadAllBytes(indexPath);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(afterIndex, File.ReadAllBytes(indexPath));

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("model.txt", change.Path);
    }

    [Fact]
    public async Task GitDirectoryLinkRepresentationDiffersFromRawCapture()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var first = workspace.CreateDirectory("first").FullName;
        var second = workspace.CreateDirectory("second").FullName;
        File.WriteAllText(Path.Combine(first, "model.txt"), "same content\n");
        File.WriteAllText(Path.Combine(second, "model.txt"), "same content\n");
        var link = Path.Combine(root, "link");
        await PipelineOutputGitTestHelper.CreateDirectoryLinkAsync(link, first, junction: OperatingSystem.IsWindows());
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/link");
        var entries = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/link");
        Assert.StartsWith(OperatingSystem.IsWindows() ? "100644 " : "120000 ", entries);
        var indexPath = Path.Combine(workspace.Path, ".git", "index");
        var index = File.ReadAllBytes(indexPath);
        var objects = ReadObjects(workspace.Path);
        var raw = new PipelineOutputSnapshotter();
        var rawBefore = await raw.CaptureAsync(root, TestContext.Current.CancellationToken);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Directory.Delete(link);
        await PipelineOutputGitTestHelper.CreateDirectoryLinkAsync(link, second, junction: OperatingSystem.IsWindows());
        // Porcelain v2 emits "1 A. N... <mH> <mI> <mW> <hH> <hI> path\0".
        // Y is '.' for the unchanged junction contents, but 'M' for a changed Unix symlink target.
        var status = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "status",
            "--porcelain=v2", "-z", "--untracked-files=no", "--no-renames");
        Assert.Equal(OperatingSystem.IsWindows() ? '.' : 'M', Assert.Single(status.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Split(' ')[1][1]);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);
        var rawAfter = await raw.CaptureAsync(root, TestContext.Current.CancellationToken);
        var rawChange = Assert.Single(DefaultPipelineOutputComparer.Compare(rawBefore, rawAfter, root));
        Assert.Equal(PipelineOutputChangeKind.Changed, rawChange.Kind);
        Assert.Equal("link", rawChange.Path);
        Assert.Equal(index, File.ReadAllBytes(indexPath));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(changes);
        }
        else
        {
            var change = Assert.Single(changes);
            Assert.Equal(rawChange.Kind, change.Kind);
            Assert.Equal(rawChange.Path, change.Path);
        }
    }

    private static async Task InitializeAsync(string directory, string objectFormat)
    {
        await PipelineOutputGitTestHelper.RunAsync(directory, "init", "--object-format=" + objectFormat);
        await PipelineOutputGitTestHelper.RunAsync(directory, "config", "core.autocrlf", "false");
        await PipelineOutputGitTestHelper.RunAsync(directory, "config", "core.excludesFile", Path.Combine(directory, ".git", "info", "exclude"));
    }

    [Theory]
    [InlineData("sha1", false)]
    [InlineData("sha256", false)]
    [InlineData("sha1", true)]
    [InlineData("sha256", true)]
    public async Task StagingAnUnchangedLinkPreservesItsFingerprint(string objectFormat, bool directoryLink)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, objectFormat);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.symlinks", "true");
        var root = workspace.CreateDirectory("output").FullName;
        var target = Path.Combine(workspace.Path, "target.txt");
        File.WriteAllText(target, "destination content, not the link blob");
        var link = Path.Combine(root, "link");
        if (directoryLink)
        {
            target = workspace.CreateDirectory("target").FullName;
            File.WriteAllText(Path.Combine(target, "content.txt"), "target content must not be hashed");
            Directory.CreateSymbolicLink(link, target);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/link");
        var entry = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/link");
        Assert.StartsWith("120000 ", entry);
        var oid = entry.Split(' ')[1];
        Assert.Equal(objectFormat == "sha1" ? 40 : 64, oid.Length);
        var linkBlob = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "cat-file", "blob", oid);
        Assert.Equal(OperatingSystem.IsWindows() ? target.Replace('\\', '/') : target, linkBlob);
        var index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        var objects = ReadObjects(workspace.Path);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));

        var staged = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.True(staged.Files[link].IsLink);
        Assert.Equal("120000", staged.Files[link].GitMode);
        var contentPath = directoryLink ? Path.Combine(target, "content.txt") : target;
        File.WriteAllText(contentPath, "changed destination contents");
        var sameTarget = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(staged, sameTarget, TestContext.Current.CancellationToken));
        var other = directoryLink ? workspace.CreateDirectory("other-target").FullName : Path.Combine(workspace.Path, "other-target.txt");
        File.WriteAllText(directoryLink ? Path.Combine(other, "content.txt") : other, File.ReadAllText(contentPath));
        Assert.Equal(File.ReadAllText(contentPath), File.ReadAllText(directoryLink ? Path.Combine(other, "content.txt") : other));
        if (directoryLink)
        {
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, other);
        }
        else
        {
            File.Delete(link);
            File.CreateSymbolicLink(link, other);
        }
        Assert.NotEqual(target, other);
        Assert.Equal(other, new FileInfo(link).LinkTarget);
        var retargeted = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(staged, retargeted, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("link", change.Path);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task StagingAnUnchangedOrdinaryFileDoesNotInventAModeChange(string objectFormat)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, objectFormat);
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        File.WriteAllText(path, "same content\n");
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var before = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.False(before.Files[path].IsLink);
        Assert.Null(before.Files[path].GitMode);

        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        var index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        var objects = ReadObjects(workspace.Path);
        var staged = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal("100644", staged.Files[path].GitMode);
        Assert.Equal(before.Files[path].Hash.ToArray(), staged.Files[path].Hash.ToArray());
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(before, staged, TestContext.Current.CancellationToken));
        File.WriteAllText(path, "real change\n");
        var changed = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(staged, changed, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("model.txt", change.Path);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
    }

    [Fact]
    public async Task CleanIndexReuseDoesNotInvokeARequiredFilterForStatCleanFiles()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        File.WriteAllText(path, "clean content\n");
        // Make Git's stat-clean precondition deterministic without sleeps or index-ignore hints.
        // A file timestamp older than the index avoids Git's racy-clean internal rehash path.
        File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        Assert.True(File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(Path.Combine(workspace.Path, ".git", "index")));
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "*.txt filter=reject\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.clean", "git pipeline-output-filter-must-fail");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.required", "true");
        var status = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "status",
            "--porcelain=v2", "-z", "--untracked-files=no", "--no-renames");
        Assert.Equal('.', Assert.Single(status.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Split(' ')[1][1]);
        var index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        var objects = ReadObjects(workspace.Path);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));

        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
    }

    [Fact]
    public async Task UntrackedRegularFilesUseOneSerialGitBatchPerCapture()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        const int count = 3;
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(root, $"model {i:D2} ü.txt"), $"content {i}\n");
        }
        var clean = Path.Combine(root, "clean.keep");
        File.WriteAllText(clean, "tracked and stat-clean\n");
        File.SetLastWriteTimeUtc(clean, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/clean.keep");
        Assert.True(File.GetLastWriteTimeUtc(clean) < File.GetLastWriteTimeUtc(Path.Combine(workspace.Path, ".git", "index")));
        var log = Path.Combine(workspace.Path, "filter-pids");
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "*.txt filter=record\n*.keep filter=reject\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.clean", "git pipeline-output-filter-must-fail");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.required", "true");
        // Git's MSYS shell reports PPID=1 for a native Windows parent. Query the native process
        // tree there instead; on Unix PPID already identifies the parent Git process. Recording
        // the actual Git PID detects both a process per file and multiple parallel batches.
        var parentCommand = OperatingSystem.IsWindows()
            ? "powershell.exe -NoProfile -NonInteractive -Command '$ErrorActionPreference=\"Stop\"; $p=Get-CimInstance Win32_Process -Filter (\"ProcessId=\"+$PID); do { $p=Get-CimInstance Win32_Process -Filter (\"ProcessId=\"+$p.ParentProcessId) } while ($p -and $p.Name -ne \"git.exe\"); if (!$p) { throw \"No Git ancestor\" }; $p.ProcessId'"
            : "printf '%s\\n' \"$PPID\"";
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.record.clean",
            parentCommand + " >> '" + log.Replace('\\', '/').Replace("'", "'\\''") + "' && cat");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.record.required", "true");
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));

        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var beforePids = File.ReadAllLines(log);
        Assert.Equal(count, beforePids.Length);
        Assert.True(int.Parse(Assert.Single(beforePids.Distinct()), System.Globalization.CultureInfo.InvariantCulture) > 1);
        File.Delete(log);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        var afterPids = File.ReadAllLines(log);
        Assert.Equal(count, afterPids.Length);
        Assert.True(int.Parse(Assert.Single(afterPids.Distinct()), System.Globalization.CultureInfo.InvariantCulture) > 1);
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("--skip-worktree", false)]
    [InlineData("--assume-unchanged", false)]
    [InlineData("--skip-worktree", true)]
    [InlineData("--assume-unchanged", true)]
    public async Task IndexHintsDoNotHideChangesToPresentOutput(string hint, bool changeMode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        File.WriteAllText(path, "before\n");
        if (changeMode)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Assert.Equal(0, (int)(File.GetUnixFileMode(path) & UnixFileMode.UserExecute));
            }
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.filemode", "false");
        }
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        if (changeMode)
        {
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", "--chmod=+x", "--", "output/model.txt");
            Assert.StartsWith("100755 ", await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/model.txt"));
        }
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", hint, "--", "output/model.txt");
        var index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        if (changeMode)
        {
            // The capture uses Git's reported index mode, not an inferred filesystem mode.
            // Keep the hint in place while changing the mode Git reports for the same content.
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", "--chmod=-x", "--", "output/model.txt");
            Assert.StartsWith("100644 ", await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/model.txt"));
            index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        }
        else
        {
            File.WriteAllText(path, "after!\n");
        }
        var status = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "--no-optional-locks", "status",
            "--porcelain=v2", "-z", "--untracked-files=no", "--no-renames");
        Assert.Equal('.', Assert.Single(status.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Split(' ')[1][1]);

        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal(Path.GetRelativePath(root, path), change.Path);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
    }

    [Theory]
    [InlineData("sha1", "")]
    [InlineData("sha1", "--skip-worktree")]
    [InlineData("sha1", "--assume-unchanged")]
    [InlineData("sha256", "--skip-worktree")]
    public async Task IndexHintsAndCoreSymlinksUseWorkingEntryKind(string objectFormat, string hint)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, objectFormat);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.symlinks", "true");
        var root = workspace.CreateDirectory("output").FullName;
        var target = Path.Combine(workspace.Path, "target.txt");
        File.WriteAllText(target, "destination, not the symlink blob");
        var path = Path.Combine(root, "model.txt");
        File.CreateSymbolicLink(path, target);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        var entry = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--stage", "--", "output/model.txt");
        Assert.StartsWith("120000 ", entry);
        var canonicalTarget = OperatingSystem.IsWindows() ? target.Replace('\\', '/') : target;
        Assert.Equal(canonicalTarget, await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "cat-file", "blob", entry.Split(' ')[1]));
        File.Delete(path);
        File.WriteAllText(path, canonicalTarget);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.symlinks", "false");
        if (hint.Length != 0)
        {
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", hint, "--", "output/model.txt");
        }
        var index = File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index"));
        var objects = ReadObjects(workspace.Path);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Null(new FileInfo(path).LinkTarget);

        var storageBefore = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.False(storageBefore.Files[path].IsLink);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));

        // A link index entry cannot supply the fingerprint for an ordinary storage file,
        // even if index-ignore hints keep its working-tree status clean.
        File.WriteAllText(path, canonicalTarget + ".retargeted");
        var storageAfter = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.False(storageAfter.Files[path].IsLink);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(storageBefore, storageAfter, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal(Path.GetRelativePath(root, path), change.Path);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(workspace.Path, ".git", "index")));
        AssertObjectsEqual(objects, ReadObjects(workspace.Path));
    }

    [Fact]
    public async Task GitCapturesRootsWithPlatformPathComparison()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        File.WriteAllText(path, "before\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/model.txt");
        var requested = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        Assert.True(Directory.Exists(requested));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(requested, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "after!\n");

        var afterSnapshot = await nativeSnapshotter.CaptureAsync(requested, TestContext.Current.CancellationToken);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.True(string.Equals(Path.GetRelativePath(requested, path), change.Path, PipelineOutputRoot.PathComparison));
    }

    [Fact]
    public async Task ChosenGitHashFailureDoesNotFallBackToRawContent()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeAsync(workspace.Path, "sha1");
        var root = workspace.CreateDirectory("output").FullName;
        File.WriteAllText(Path.Combine(root, "model.txt"), "untracked content\n");
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Single(beforeSnapshot.Files);
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "*.txt filter=reject\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.clean", "git pipeline-output-filter-must-fail");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.required", "true");

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken));

        Assert.Contains("filter", failure.Message);
    }

    private static Dictionary<string, byte[]> ReadObjects(string directory) =>
        Directory.EnumerateFiles(Path.Combine(directory, ".git", "objects"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);

    private static void AssertObjectsEqual(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, after[path]);
        }
    }
}

#pragma warning restore ASPIREPIPELINES005
