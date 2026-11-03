// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

public sealed class PipelineOutputStrategyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FilesystemChoiceNeverInvokesGitAndComparesRawEol()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Path.Combine(workspace.Path, "artifact.txt");
        File.WriteAllText(path, "line\n");
        var runner = new PipelineOutputTestProcessRunner(_ => throw new InvalidOperationException("Git must not run"));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem }));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "line\r\n");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineOutputChangeKind.Changed, Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken)).Kind);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".git/objects/content")]
    [InlineData("nested/.git")]
    public async Task NativeFilesystemRulesControlGitMetadataWithoutInvokingGit(string metadataPath)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "artifact.txt");
        File.WriteAllText(artifact, "content");
        var metadata = Path.Combine(workspace.Path, metadataPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllText(metadata, "metadata");
        Assert.True(File.Exists(metadata));
        var runner = new PipelineOutputTestProcessRunner(_ => throw new InvalidOperationException("Git must not run"));
        var options = new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem };
        Assert.Empty(options.Rules);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(options));

        var snapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { artifact, metadata }.Order(PipelineOutputRoot.PathComparer), snapshot.Files.Keys.Order(PipelineOutputRoot.PathComparer));

        options.Rules.Add(new() { Action = "Include", Pattern = "**/*" });
        options.Rules.Add(new() { Action = "Exclude", Pattern = "**/.git" });
        options.Rules.Add(new() { Action = "Exclude", Pattern = "**/.git/**" });
        nativeSnapshotter = new(new(), runner, Options.Create(options));
        snapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { artifact }, snapshot.Files.Keys);

        options.Rules.Add(new() { Action = "Include", Pattern = "**/*" });
        nativeSnapshotter = new(new(), runner, Options.Create(options));
        snapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { artifact, metadata }.Order(PipelineOutputRoot.PathComparer), snapshot.Files.Keys.Order(PipelineOutputRoot.PathComparer));
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredGitRejectsUnavailableExecutableOrWorktree(bool missingExecutable)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var options = new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.Git };
        var runner = new PipelineOutputTestProcessRunner(_ => missingExecutable
            ? throw new Win32Exception(2)
            : new(128, "", "unavailable in any language"));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(options));
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken));
        Assert.Contains("Git", failure.Message);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(128, "")]
    [InlineData(0, "false\n")]
    public async Task AutoUnavailableDiscoveryFallsBackPerCaptureWithoutInterpretingStderr(int exitCode, string discovery)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Path.Combine(workspace.Path, "artifact.txt");
        File.WriteAllText(path, "before");
        var runner = new PipelineOutputTestProcessRunner(_ => new(exitCode, discovery, "診断は解析しません"));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Single(runner.Calls);
        File.WriteAllText(path, "after!");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineOutputChangeKind.Changed, Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, args => Assert.Contains("rev-parse", args));
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.Auto)]
    [InlineData(PipelineOutputDiffMode.FileSystem)]
    [InlineData(PipelineOutputDiffMode.Git)]
    public async Task BothMissingCapturesStayEmptyWithoutDiscovery(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var missing = Path.Combine(workspace.Path, "missing");
        Assert.False(Directory.Exists(missing));
        var runner = new PipelineOutputTestProcessRunner(_ => throw new InvalidOperationException("Missing roots must not probe Git"));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions { Mode = mode }));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(missing, TestContext.Current.CancellationToken);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(missing, TestContext.Current.CancellationToken);
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.Auto)]
    [InlineData(PipelineOutputDiffMode.FileSystem)]
    [InlineData(PipelineOutputDiffMode.Git)]
    public async Task MissingBaselineReportsSelectedGeneratedFileAsAdded(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var directory = Path.Combine(workspace.Path, "missing");
        Assert.False(Directory.Exists(directory));
        var runner = new PipelineOutputTestProcessRunner(args => args.Contains("rev-parse")
            ? mode is PipelineOutputDiffMode.Git ? new(0, "true\n\n", "") : new(128, "", "unavailable")
            : new(0, args.Contains("--others") ? "artifact.txt\0" : args.Contains("hash-object") ? new string('a', 40) + "\n" : "", ""));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions { Mode = mode }));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(directory, TestContext.Current.CancellationToken);
        Assert.Empty(runner.Calls);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "artifact.txt");
        File.WriteAllText(path, "generated");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(directory, TestContext.Current.CancellationToken);
        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Added, change.Kind);
        Assert.Equal(Path.GetRelativePath(directory, path), change.Path);
        Assert.Equal(mode is PipelineOutputDiffMode.FileSystem ? 0 : 1, runner.Calls.Count(args => args.Contains("rev-parse")));
    }

    [Theory]
    [InlineData("true\n", "lines")]
    [InlineData("true\n../unexpected\n", "parent path")]
    [InlineData("maybe\n\n", "parent path")]
    [InlineData("true\n\n\n", "lines")]
    [InlineData("true\r\r\n\r\n", "parent path")]
    [InlineData("true\r\n../\r\r\n", "parent path")]
    [InlineData("true\n..\\\n", "parent path")]
    public async Task MalformedSuccessfulDiscoveryHasAParserDiagnostic(string discovery, string field)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new PipelineOutputTestProcessRunner(_ => new(0, discovery, ""));
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => GitPipelineOutputReader.TryCreateAsync(
            workspace.Path, runner, TestContext.Current.CancellationToken));
        Assert.Contains("malformed pipeline output worktree discovery", failure.Message);
        Assert.Equal("worktree discovery", failure.Data["GitRecordKind"]);
        Assert.Equal(field, failure.Data["GitField"]);
        Assert.Equal(discovery, failure.Data["GitValue"]);
        Assert.Equal(discovery.Length, failure.Data["GitValueLength"]);
    }

    [Theory]
    [InlineData("garbage\0")]
    [InlineData("1extra .M N... 100644 100644 100644 bad bad artifact.txt\0")]
    [InlineData("u-extra .U N... 100644 100644 100644 100644 bad bad bad artifact.txt\0")]
    [InlineData("1 .M N... 100644 100644 invalid bad bad artifact.txt\0")]
    [InlineData("1 .? N... 100644 100644 100644 bad bad artifact.txt\0")]
    [InlineData("u .U N... 100644 100644 100644 100644 bad bad bad \0")]
    public async Task MalformedStatusHasAParserDiagnostic(string status)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new PipelineOutputTestProcessRunner(args => new(0,
            args.Contains("rev-parse") ? "true\n\n" : args.Contains("status") ? status : "", ""));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions()));
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken));
        Assert.Contains("malformed pipeline output status", failure.Message);
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    public void BatchPathRejectsCrLfExplicitly(char delimiter)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Path.Combine(workspace.Path, "bad" + delimiter + "name.txt");
        Assert.Contains(delimiter, path);
        var failure = Assert.Throws<DistributedApplicationException>(() => GitPipelineOutputReader.ThrowIfUnsupportedPath(path));
        Assert.Contains("filenames containing CR or LF", failure.Message);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task NativeLinkHashUsesEngineReadTargetTextAndChecksItsResponse(bool directoryLink, bool malformed, bool indexed)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("output").FullName;
        var target = directoryLink ? workspace.CreateDirectory("target").FullName : Path.Combine(workspace.Path, "target.txt");
        if (!directoryLink)
        {
            File.WriteAllText(target, "target content must not be hashed");
        }
        var path = Path.Combine(root, "link");
        if (directoryLink)
        {
            Directory.CreateSymbolicLink(path, target);
        }
        else
        {
            File.CreateSymbolicLink(path, target);
        }
        Assert.Equal(target, new FileInfo(path).LinkTarget);
        var oid = new string('b', 40);
        var runner = new PipelineOutputTestProcessRunner(args => new(0,
            args.Contains("hash-object") ? (malformed ? "invalid" : oid) + "\n" :
            args.Contains("rev-parse") ? "true\n\n" :
            args.Contains("--stage") && indexed ? "H 120000 " + new string('a', 40) + " 0\tlink\0" :
            args.Contains("--others") && !indexed ? "link\0" : "", ""));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions()));
        if (malformed)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken));
            Assert.Contains("malformed pipeline output link object ID", failure.Message);
        }
        else
        {
            var snapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
            Assert.True(snapshot.Files[path].IsLink);
            Assert.Equal(indexed ? "120000" : null, snapshot.Files[path].GitMode);
            Assert.Equal(Encoding.ASCII.GetBytes(oid), snapshot.Files[path].Hash.ToArray());
        }
        Assert.Equal(new[] { "rev-parse", "ls-files", "ls-files", "status", "hash-object" }, runner.Calls.Select(args => args[1]));
        Assert.Equal(new[] { "--no-optional-locks", "hash-object", "--stdin" }, runner.Calls[^1]);
        Assert.Equal(Encoding.UTF8.GetBytes(new FileInfo(path).LinkTarget!), runner.Inputs[^1]);
    }

    [Theory]
    [InlineData("missing-tab")]
    [InlineData("H 100644 abc 0\tartifact.txt\0")]
    public async Task MalformedIndexHasAParserDiagnostic(string index)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new PipelineOutputTestProcessRunner(args => new(0, args.Contains("rev-parse") ? "true\n\n" : index, ""));
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(workspace.Path, runner, TestContext.Current.CancellationToken));
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => git.SelectFilesAsync(workspace.Path, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken));
        Assert.Contains("malformed pipeline output index", failure.Message);
    }

    [Theory]
    [InlineData(" space-雪.txt")]
    [InlineData("nested 雪/file café.txt")]
    [InlineData("literal\\雪 name.txt")]
    public async Task ResidualBatchUsesAbsoluteUtf8LinesWithoutQuoting(string gitPath)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() && gitPath.Contains('\\'), "Literal backslashes in filenames are Unix-only.");
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-batch-"));
        var path = Path.Combine(workspace.Path, gitPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        var runner = new PipelineOutputTestProcessRunner(args => new(0,
            args.Contains("rev-parse") ? "true\n\n" : args.Contains("--others") ? gitPath + "\0" : args.Contains("hash-object") ? new string('a', 40) + "\n" : "", ""));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(new PipelineOutputDiffOptions()));
        await nativeSnapshotter.CaptureAsync(workspace.Path, TestContext.Current.CancellationToken);
        var hashIndex = runner.Calls.FindIndex(args => args.Contains("hash-object"));
        Assert.True(hashIndex >= 0);
        var expectedPath = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        Assert.Equal(Encoding.UTF8.GetBytes(expectedPath + "\n"), runner.Inputs[hashIndex]);
    }

    [Theory]
    [InlineData("\n", "")]
    [InlineData("\r\n", "")]
    [InlineData("\n", "../")]
    [InlineData("\r\n", "../")]
    public async Task DiscoveryLineEndingsResolveSelectedPathsAgainstRepositoryRoot(string newline, string parents)
    {
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-discovery-"));
        var directory = workspace.CreateDirectory("nested").FullName;
        var path = Path.Combine(directory, "artifact.txt");
        File.WriteAllText(path, "content");
        var runner = new PipelineOutputTestProcessRunner(args => new(0,
            args.Contains("rev-parse") ? $"true{newline}{parents}{newline}" : args.Contains("--others") ? "nested/artifact.txt\0" : "", ""));
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(
            parents.Length == 0 ? workspace.Path : directory, runner, TestContext.Current.CancellationToken));

        var selected = await git.SelectFilesAsync(directory, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken);

        Assert.Equal(path, Assert.Single(selected).File.FullName);
    }

    [Theory]
    [InlineData(false, 'M', "100644", false)]
    [InlineData(true, 'U', "100755", false)]
    [InlineData(false, '.', "120000", false)]
    [InlineData(true, 'D', "160000", true)]
    [InlineData(false, '.', "000000", true)]
    [InlineData(true, 'M', "040000", false)]
    [InlineData(false, 'M', "123456", false)]
    [InlineData(true, '.', "000001", false)]
    public void StatusParsingRetainsCaptureFieldsWithoutInterpretingUnusedValues(bool unmerged, char y, string mode, bool deleted)
    {
        const string PathWithWhitespace = "output 雪/model café \t name.txt";
        // X, submodule data, ancestor/index modes and IDs are deliberately opaque here.
        // Porcelain v2 reserves eight ordinary or ten unmerged fields before the raw path.
        // https://git-scm.com/docs/git-status#_porcelain_format_version_2
        var record = unmerged
            ? $"u !{y} unused unused unused unused {mode} unused unused unused {PathWithWhitespace}"
            : $"1 !{y} unused unused unused {mode} unused unused {PathWithWhitespace}";

        var entry = GitPipelineOutputReader.StatusEntry.Parse(record);

        Assert.Equal(y, entry.Y);
        Assert.Equal(mode, entry.Mode);
        Assert.Equal(PathWithWhitespace, entry.Path);
        Assert.Equal(deleted, entry.IsDeleted);
    }

    [Theory]
    [InlineData("10064")]
    [InlineData("1006440")]
    [InlineData("100648")]
    [InlineData("10064x")]
    [InlineData("")]
    public void ModesRequireSixOctalDigits(string mode)
    {
        var index = $"H {mode} {new string('a', 40)} 0\tartifact.txt";
        var status = $"1 .M unused unused unused {mode} unused unused artifact.txt";

        var indexFailure = Assert.Throws<DistributedApplicationException>(() => GitPipelineOutputReader.IndexEntry.Parse(index));
        var statusFailure = Assert.Throws<DistributedApplicationException>(() => GitPipelineOutputReader.StatusEntry.Parse(status));

        Assert.Equal("index", indexFailure.Data["GitRecordKind"]);
        Assert.Equal("status", statusFailure.Data["GitRecordKind"]);
        Assert.Equal("record", indexFailure.Data["GitField"]);
        Assert.Equal("record", statusFailure.Data["GitField"]);
    }

    [Theory]
    [InlineData("100644", 'H', '0', 40, true, true, false, true)]
    [InlineData("100755", 'h', '0', 64, false, true, false, true)]
    [InlineData("120000", 'S', '1', 40, false, false, false, false)]
    [InlineData("160000", 's', '2', 64, false, false, true, false)]
    [InlineData("040000", 'M', '3', 40, false, false, false, false)]
    [InlineData("123456", '?', '0', 64, false, false, false, true)]
    public void IndexParsingRetainsOpaqueModesHintsStagesAndHashes(string mode, char tag, char stage, int idLength,
        bool reusable, bool regular, bool gitLink, bool merged)
    {
        var oid = new string('A', idLength);
        const string PathWithWhitespace = "output 雪/model café \t name.txt";
        var record = $"{tag} {mode} {oid} {stage}\t{PathWithWhitespace}";

        var entry = GitPipelineOutputReader.IndexEntry.Parse(record);

        Assert.Equal(mode, entry.Mode);
        Assert.Equal(tag, entry.Tag);
        Assert.Equal(stage, entry.Stage);
        Assert.Equal(Encoding.ASCII.GetBytes(oid), entry.Hash.ToArray());
        Assert.Equal(PathWithWhitespace, entry.Path);
        Assert.Equal(reusable, entry.IsReusable);
        Assert.Equal(regular, entry.IsRegularFile);
        Assert.Equal(gitLink, entry.IsGitLink);
        Assert.Equal(merged, entry.IsMerged);
    }

    [Theory]
    [InlineData("X", "100644", "0", "artifact.txt")]
    [InlineData("HH", "100644", "0", "artifact.txt")]
    [InlineData("H", "000000", "0", "artifact.txt")]
    [InlineData("H", "100644", "4", "artifact.txt")]
    [InlineData("H", "100644", "00", "artifact.txt")]
    [InlineData("H", "100644", "0", "")]
    public void InvalidIndexHeadersRetainRecordContext(string tag, string mode, string stage, string path)
    {
        var record = $"{tag} {mode} {new string('a', 40)} {stage}\t{path}";

        var failure = Assert.Throws<DistributedApplicationException>(() => GitPipelineOutputReader.IndexEntry.Parse(record));

        Assert.Equal("index", failure.Data["GitRecordKind"]);
        Assert.Equal("record", failure.Data["GitField"]);
        Assert.Equal(record, failure.Data["GitValue"]);
        Assert.Equal(record.Length, failure.Data["GitValueLength"]);
    }

    [Theory]
    [InlineData("index", "", true)]
    [InlineData("untracked paths", "", true)]
    [InlineData("status", "", true)]
    [InlineData("index", "\0", false)]
    [InlineData("untracked paths", "\0", false)]
    [InlineData("status", "\0", false)]
    [InlineData("index", "unterminated", false)]
    [InlineData("untracked paths", "unterminated", false)]
    [InlineData("status", "unterminated", false)]
    [InlineData("index", "record\0\0", false)]
    [InlineData("untracked paths", "record\0\0", false)]
    [InlineData("status", "record\0\0", false)]
    [InlineData("index", "\0record\0", false)]
    [InlineData("untracked paths", "\0record\0", false)]
    [InlineData("status", "\0record\0", false)]
    public async Task TypedReadsValidateNulRecordFraming(string kind, string response, bool valid)
    {
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-framing-"));
        var runner = new PipelineOutputTestProcessRunner(args => new(0,
            (kind == "index" && args.Contains("--stage")) || (kind == "untracked paths" && args.Contains("--others")) ||
            (kind == "status" && args.Contains("status")) ? response : "", ""));
        var git = new GitPipelineOutputReader(workspace.Path, runner);
        Func<Task> read = async () =>
        {
            if (kind == "status")
            {
                Assert.Empty(await git.ReadStatusAsync(TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.Empty(await git.SelectFilesAsync(workspace.Path, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken));
            }
        };
        if (valid)
        {
            await read();
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(read);
            Assert.Equal(kind, failure.Data["GitRecordKind"]);
            Assert.Equal("record framing", failure.Data["GitField"]);
            Assert.Equal(response, failure.Data["GitValue"]);
            Assert.Equal(response.Length, failure.Data["GitValueLength"]);
        }
    }

    [Theory]
    [InlineData(40, "\n")]
    [InlineData(40, "\r\n")]
    [InlineData(64, "\n")]
    [InlineData(64, "\r\n")]
    public async Task TypedHashesRetainObjectIdsWithGitLineEndings(int idLength, string newline)
    {
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-ids-"));
        var oid = new string('A', idLength);
        var runner = new PipelineOutputTestProcessRunner(_ => new(0, oid + newline, ""));
        var git = new GitPipelineOutputReader(workspace.Path, runner);

        var linkHash = await git.HashLinkTargetAsync("destination 雪", TestContext.Current.CancellationToken);
        var fileHashes = await git.HashFilesAsync([new FileInfo(Path.Combine(workspace.Path, "artifact.txt"))], TestContext.Current.CancellationToken);

        Assert.Equal(Encoding.ASCII.GetBytes(oid), linkHash.ToArray());
        Assert.Equal(Encoding.ASCII.GetBytes(oid), Assert.Single(fileHashes).ToArray());
    }

    [Theory]
    [InlineData(true, 0, 'a')]
    [InlineData(true, 39, 'a')]
    [InlineData(false, 41, 'a')]
    [InlineData(true, 63, 'a')]
    [InlineData(true, 65, 'a')]
    [InlineData(true, 40, 'g')]
    [InlineData(false, 64, 'g')]
    [InlineData(false, 255, 'g')]
    [InlineData(false, 256, 'g')]
    [InlineData(false, 257, 'g')]
    public async Task InvalidHashIdsIncludeBoundedFieldContext(bool link, int length, char character)
    {
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-diagnostic-"));
        // Wrong-length IDs use valid hex so they cannot pass by failing character validation.
        // A misbehaving filter's long diagnostic has a distinct prefix to verify truncation direction.
        const string DiagnosticPrefix = "filter diagnostic: ";
        var invalid = length >= 255 ? DiagnosticPrefix + new string(character, length - DiagnosticPrefix.Length) : new string(character, length);
        Assert.Equal(length, invalid.Length);
        var runner = new PipelineOutputTestProcessRunner(_ => new(0, invalid + "\n", ""));
        var git = new GitPipelineOutputReader(workspace.Path, runner);

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
        {
            if (link)
            {
                await git.HashLinkTargetAsync("target", TestContext.Current.CancellationToken);
            }
            else
            {
                await git.HashFilesAsync([new FileInfo(Path.Combine(workspace.Path, "artifact.txt"))], TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal(link ? "link object ID" : "object IDs", failure.Data["GitRecordKind"]);
        Assert.Equal("object ID", failure.Data["GitField"]);
        Assert.Equal(invalid[..Math.Min(length, 256)], failure.Data["GitValue"]);
        Assert.Equal(length, failure.Data["GitValueLength"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedOutputMergesOptionalAdditionalData(bool? includeAdditionalData)
    {
        IEnumerable<KeyValuePair<string, object?>>? additionalData = includeAdditionalData switch
        {
            null => null,
            false => [],
            true => [new("Operation", "hashing"), new("Detail", null)]
        };
        Assert.Equal(includeAdditionalData.HasValue, additionalData is not null);
        Assert.Equal(includeAdditionalData is true ? 2 : 0, additionalData?.Count() ?? 0);

        var failure = Assert.Throws<DistributedApplicationException>(() =>
            GitPipelineOutputReader.ThrowMalformed("index", "record", "invalid record", additionalData));

        Assert.Equal("index", failure.Data["GitRecordKind"]);
        Assert.Equal("record", failure.Data["GitField"]);
        Assert.Equal("invalid record", failure.Data["GitValue"]);
        Assert.Equal("invalid record".Length, failure.Data["GitValueLength"]);
        Assert.Equal(includeAdditionalData is true ? 6 : 4, failure.Data.Count);
        if (includeAdditionalData is true)
        {
            Assert.Equal("hashing", failure.Data["Operation"]);
            Assert.Contains("Detail", failure.Data.Keys.Cast<string>());
            Assert.Null(failure.Data["Detail"]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeBatchMappingRequiresOneObjectIdPerSelectedFile(int actualCount)
    {
        using var workspace = new TemporaryWorkspace(output, Directory.CreateTempSubdirectory("aspire-output-count-"));
        var first = Path.Combine(workspace.Path, "first.txt");
        var second = Path.Combine(workspace.Path, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var ids = string.Concat(Enumerable.Range(0, actualCount).Select(i => new string((char)('a' + i), 40) + "\n"));
        var runner = new PipelineOutputTestProcessRunner(args => new(0,
            args.Contains("--others") ? "first.txt\0second.txt\0" : args.Contains("hash-object") ? ids : "", ""));
        var git = new GitPipelineOutputReader(workspace.Path, runner);
        var snapshotter = new PipelineOutputSnapshotter();
        if (actualCount == 2)
        {
            var snapshot = await snapshotter.CaptureAsync(workspace.Path, PipelineOutputGitTestHelper.AllFilesMatcher(), git, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { first, second }, snapshot.Keys);
            Assert.Equal(Encoding.ASCII.GetBytes(new string('a', 40)), snapshot[first].Hash.ToArray());
            Assert.Equal(Encoding.ASCII.GetBytes(new string('b', 40)), snapshot[second].Hash.ToArray());
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => snapshotter.CaptureAsync(
                workspace.Path, PipelineOutputGitTestHelper.AllFilesMatcher(), git, TestContext.Current.CancellationToken));
            Assert.Equal("object IDs", failure.Data["GitRecordKind"]);
            Assert.Equal("count", failure.Data["GitField"]);
            Assert.Equal(2, failure.Data["ExpectedCount"]);
            Assert.Equal(actualCount, failure.Data["ActualCount"]);
        }
    }

    [Fact]
    public async Task CustomCallbackUsesAvailableIndexedPathsWithoutNativeStatusDeletionPolicy()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init", "--object-format=sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var child = workspace.CreateDirectory("output/ignored-child").FullName;
        var path = Path.Combine(child, "model.txt");
        File.WriteAllText(path, "indexed content");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/ignored-child/model.txt");
        File.WriteAllText(Path.Combine(workspace.Path, ".gitignore"), "output/ignored-child*\n");
        Directory.Delete(child, recursive: true);
        var target = workspace.CreateDirectory("external").FullName;
        var external = Path.Combine(target, "model.txt");
        File.WriteAllText(external, "available through indexed name");
        Directory.CreateSymbolicLink(child, target);
        Assert.Equal("available through indexed name", File.ReadAllText(path));
        Assert.Equal("output/ignored-child/model.txt\0", await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
            "ls-files", "--cached", "-z", "--", "output"));
        Assert.Empty(await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
            "ls-files", "--others", "--exclude-standard", "-z", "--", "output"));
        var status = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "status", "--porcelain=v2", "-z", "--untracked-files=no", "--no-renames");
        Assert.Equal('D', Assert.Single(status.Split('\0', StringSplitOptions.RemoveEmptyEntries)).Split(' ')[1][1]);
        var native = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        Assert.Empty((await native.CaptureAsync(root, TestContext.Current.CancellationToken)).Files);
        var calls = new ConcurrentBag<string>();
        var custom = new DefaultPipelineOutputSnapshotter(new PipelineOutputSnapshotter(new TestPipelineOutputFileHasher(async (file, token) =>
        {
            calls.Add(file.FullName);
            using var reader = File.OpenText(file.FullName);
            return Encoding.UTF8.GetBytes(await reader.ReadToEndAsync(token));
        })), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));

        var beforeSnapshot = await custom.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { path }, calls);
        var unchanged = await custom.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes("available through indexed name"), unchanged.Files[path].Hash.ToArray());
        File.WriteAllText(external, "changed through indexed name");
        var afterSnapshot = await custom.CaptureAsync(root, TestContext.Current.CancellationToken);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal(Path.Combine("ignored-child", "model.txt"), change.Path);
        Assert.Equal(new[] { path, path, path }, calls);
        Assert.Empty((await native.CaptureAsync(root, TestContext.Current.CancellationToken)).Files);
    }

    [Theory]
    [InlineData(false, false, PipelineOutputDiffMode.FileSystem)]
    [InlineData(true, false, PipelineOutputDiffMode.FileSystem)]
    [InlineData(false, true, PipelineOutputDiffMode.FileSystem)]
    [InlineData(true, true, PipelineOutputDiffMode.FileSystem)]
    [InlineData(false, false, PipelineOutputDiffMode.Git)]
    [InlineData(true, false, PipelineOutputDiffMode.Git)]
    [InlineData(false, true, PipelineOutputDiffMode.Git)]
    [InlineData(true, true, PipelineOutputDiffMode.Git)]
    [InlineData(false, false, PipelineOutputDiffMode.Auto)]
    [InlineData(true, false, PipelineOutputDiffMode.Auto)]
    [InlineData(false, true, PipelineOutputDiffMode.Auto)]
    [InlineData(true, true, PipelineOutputDiffMode.Auto)]
    public async Task ExplicitCallbacksOwnContentAndFailuresInsideWorktree(bool throughDi, bool throwing, PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init", "--object-format=sha1");
        var root = workspace.CreateDirectory("output").FullName;
        var path = Path.Combine(root, "model.txt");
        File.WriteAllText(path, "before\n");
        File.WriteAllText(Path.Combine(root, "clean.txt"), "clean\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output");
        // Explicit callbacks own content policy, including failures. Git membership must not run
        // status/clean conversion first and replace the callback's result or exception.
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "*.txt filter=reject\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.clean", "git pipeline-output-filter-must-fail");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "filter.reject.required", "true");
        var calls = 0;
        var streams = new ConcurrentBag<Stream>();
        var hasher = new TestPipelineOutputFileHasher(async (file, ct) =>
        {
            using var stream = File.OpenRead(file.FullName);
            Interlocked.Increment(ref calls);
            streams.Add(stream);
            if (throwing)
            {
                throw new IOException("explicit callback failure");
            }
            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer, ct));
            return new byte[] { 42 };
        });
        var snapshotter = new PipelineOutputSnapshotter(hasher);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: root, step: "publish-and-diff");
        builder.Services.AddSingleton<IPipelineOutputFileHasher>(hasher);
        builder.WithOutputDiffing();
        builder.Services.Configure<PipelineOutputDiffOptions>(options => options.Mode = mode);
        using var app = builder.Build();
        var nativeSnapshotter = throughDi ? app.Services.GetRequiredService<IPipelineOutputSnapshotter<PipelineOutputSnapshot>>() : new DefaultPipelineOutputSnapshotter(snapshotter, new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions { Mode = mode }));
        if (throwing)
        {
            var rawFailure = await Assert.ThrowsAsync<IOException>(() => snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken));
            Assert.Equal("explicit callback failure", rawFailure.Message);
            var failure = await Assert.ThrowsAsync<IOException>(() => nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken));
            Assert.Equal("explicit callback failure", failure.Message);
            Assert.InRange(calls, 2, 4);
        }
        else
        {
            var rawBefore = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
            Assert.All(rawBefore.Values, snapshot => Assert.Null(snapshot.GitMode));
            File.WriteAllText(path, "raw control changed\n");
            var rawAfter = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
            Assert.Empty(DefaultPipelineOutputComparer.Compare(rawBefore, rawAfter, root));
            Assert.Equal(4, calls);
            calls = 0;
            var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(2, calls);
            File.WriteAllText(path, "snapshotter changed\n");
            var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(4, calls);
            Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
            var customCapture = await Assert.IsType<DefaultPipelineOutputSnapshotter>(nativeSnapshotter).CaptureAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(2, customCapture.Files.Count);
            Assert.All(customCapture.Files.Values, snapshot => Assert.Null(snapshot.GitMode));
        }
        Assert.All(streams, stream => Assert.False(stream.CanRead));
    }
}

#pragma warning restore ASPIREPIPELINES005
