// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

public sealed class PipelineOutputSelectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(PipelineOutputDiffMode.FileSystem)]
    [InlineData(PipelineOutputDiffMode.Git)]
    [InlineData(PipelineOutputDiffMode.Auto)]
    public async Task OrderedRulesRestoreExcludedParentsBeforeGitFiltering(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output [literal]").FullName;
        // Brackets in the output directory name must be literal in Git pathspecs.
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "src/Ev2OutputRoot/**\n", TestContext.Current.CancellationToken);
        var readme = Write(root, "README.md");
        var source = Write(root, "src/Api/Program.cs");
        var model = Write(root, "src/Ev2OutputRoot/ServiceModel.json");
        var temporary = Write(root, "src/Ev2OutputRoot/cache.tmp");
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));
        var basePaths = (await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken)).Select(entry => entry.File.FullName).ToArray();
        Assert.Contains(source, basePaths);
        Assert.DoesNotContain(model, basePaths);
        var options = new PipelineOutputDiffOptions { Mode = mode };
        options.Rules.Add(new() { Action = "Include", Pattern = "**/*" });
        options.Rules.Add(new() { Action = "Exclude", Pattern = ".gitignore" });
        options.Rules.Add(new() { Action = "exclude", Pattern = "src/**" });
        options.Rules.Add(new() { Action = "INCLUDE", Pattern = "src/Ev2OutputRoot/**" });
        options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*.tmp" });

        var selected = await SelectAsync(options, root);

        Assert.Equal(mode is not PipelineOutputDiffMode.FileSystem ? new[] { readme } : new[] { readme, model }, selected.Order(PipelineOutputRoot.PathComparer));
        Assert.DoesNotContain(source, selected);
        Assert.DoesNotContain(temporary, selected);
    }

    [Theory]
    [InlineData(false, PipelineOutputDiffMode.FileSystem)]
    [InlineData(false, PipelineOutputDiffMode.Git)]
    [InlineData(true, PipelineOutputDiffMode.FileSystem)]
    [InlineData(true, PipelineOutputDiffMode.Git)]
    [InlineData(false, PipelineOutputDiffMode.Auto)]
    [InlineData(true, PipelineOutputDiffMode.Auto)]
    public async Task ExplicitIncludesStillApplyGitToNewAndExistingIgnoredArtifacts(bool existsBefore, PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "output/**\n", TestContext.Current.CancellationToken);
        var root = workspace.CreateDirectory("output").FullName;
        var artifact = Path.Combine(root, "artifacts", "model.json");
        if (existsBefore)
        {
            Write(root, "artifacts/model.json", "old");
        }
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));
        Assert.Empty(await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken));
        Assert.Equal(existsBefore, File.Exists(artifact));
        var options = new PipelineOutputDiffOptions { Mode = mode };
        options.Rules.Add(new() { Action = "Include", Pattern = "artifacts/**/*.json" });
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(options));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Write(root, "artifacts/model.json", "new");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);

        if (mode is not PipelineOutputDiffMode.FileSystem)
        {
            Assert.Empty(changes);
        }
        else
        {
            var change = Assert.Single(changes);
            Assert.Equal(Path.GetRelativePath(root, artifact), change.Path);
            Assert.Equal(existsBefore ? PipelineOutputChangeKind.Changed : PipelineOutputChangeKind.Added, change.Kind);
        }
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.FileSystem, false)]
    [InlineData(PipelineOutputDiffMode.FileSystem, true)]
    [InlineData(PipelineOutputDiffMode.Git, false)]
    [InlineData(PipelineOutputDiffMode.Git, true)]
    [InlineData(PipelineOutputDiffMode.Auto, false)]
    [InlineData(PipelineOutputDiffMode.Auto, true)]
    public async Task ConfiguredRulesReplaceImplicitIncludeAll(PipelineOutputDiffMode mode, bool includeJson)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var json = Write(root, "nested/model.json");
        Write(root, "source.cs");
        var options = new PipelineOutputDiffOptions { Mode = mode };
        options.Rules.Add(includeJson
            ? new() { Action = "Include", Pattern = "**/*.json" }
            : new() { Action = "Exclude", Pattern = "**/*.tmp" });

        var selected = await SelectAsync(options, root);

        Assert.Equal(includeJson ? new[] { json } : Array.Empty<string>(), selected);
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.Git)]
    [InlineData(PipelineOutputDiffMode.Auto)]
    public async Task GitMembershipExcludesRepositoryMetadata(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var artifact = Write(workspace.Path, "artifact.txt");
        Assert.True(File.Exists(Path.Combine(workspace.Path, ".git", "HEAD")));

        var selected = await SelectAsync(new() { Mode = mode }, workspace.Path);

        Assert.Equal(new[] { artifact }, selected);
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.FileSystem)]
    [InlineData(PipelineOutputDiffMode.Git)]
    [InlineData(PipelineOutputDiffMode.Auto)]
    public async Task NoRulesSelectAllFilesBeforeOrdinaryGitMembership(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        Write(workspace.Path, ".gitignore", "*.tmp\n");
        var root = workspace.CreateDirectory("output").FullName;
        var json = Write(root, "model.json");
        var source = Write(root, "source.cs");
        var tracked = Write(root, "tracked.tmp");
        var ignored = Write(root, "cache.tmp");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "-f", "--", "output/tracked.tmp");
        var options = new PipelineOutputDiffOptions { Mode = mode };
        Assert.Empty(options.Rules);

        var selected = await SelectAsync(options, root);

        Assert.Equal((mode is not PipelineOutputDiffMode.FileSystem ? new[] { json, source, tracked } : new[] { ignored, json, source, tracked }).Order(PipelineOutputRoot.PathComparer), selected.Order(PipelineOutputRoot.PathComparer));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScopedGlobsUseGitEntryKindsOrFilesystemNoFollow(bool useGit, bool includeLink)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        Write(workspace.Path, ".gitignore", "output/ignored/\n");
        var root = workspace.CreateDirectory("output").FullName;
        var tracked = Write(root, "ignored/child/model.json");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "-f", "--", "output/ignored/child/model.json");
        var child = Path.GetDirectoryName(tracked)!;
        Directory.Delete(child, recursive: true);
        var target = workspace.CreateDirectory("target").FullName;
        Write(target, "model.json");
        await PipelineOutputGitTestHelper.CreateDirectoryLinkAsync(child, target, junction: OperatingSystem.IsWindows());
        Assert.True((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0);
        var options = new PipelineOutputDiffOptions { Mode = useGit ? PipelineOutputDiffMode.Git : PipelineOutputDiffMode.FileSystem };
        options.Rules.Add(new() { Action = "Include", Pattern = includeLink ? "ignored/child" : "ignored/child/**" });

        var paths = await SelectAsync(options, root);

        // Git keeps indexed files under a Windows junction, but excludes the ignored,
        // untracked Unix replacement link. Filesystem capture retains the link leaf.
        var expected = useGit
            ? OperatingSystem.IsWindows() && !includeLink ? new[] { tracked } : Array.Empty<string>()
            : includeLink ? new[] { child } : Array.Empty<string>();
        Assert.Equal(expected, paths);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IgnoredIndexedAncestorLinkIsExcludedByDefaultSnapshotter(bool fileLink, bool configuredInclude)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        Write(workspace.Path, ".gitignore", "output/ignored*\n");
        var root = workspace.CreateDirectory("output").FullName;
        var link = Path.Combine(root, "ignored");
        Write(link, "model.json");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "-f", "--", "output/ignored/model.json");
        Directory.Delete(link, recursive: true);
        var first = fileLink ? Write(workspace.Path, "first.txt", "same content") : workspace.CreateDirectory("first").FullName;
        var second = fileLink ? Write(workspace.Path, "second.txt", "same content") : workspace.CreateDirectory("second").FullName;
        if (!fileLink)
        {
            Write(first, "model.json", "same content");
            Write(second, "model.json", "same content");
        }
        Assert.NotEqual(first, second);
        Assert.Equal(File.ReadAllText(fileLink ? first : Path.Combine(first, "model.json")),
            File.ReadAllText(fileLink ? second : Path.Combine(second, "model.json")));
        CreateLink(first);
        Assert.Equal(first, new FileInfo(link).LinkTarget);
        Assert.Equal("output/ignored\n", (await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
            "check-ignore", "--no-index", "--", "output/ignored")).ReplaceLineEndings("\n"));
        Assert.Equal("output/ignored/model.json\0", await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
            "ls-files", "--cached", "-z", "--", "output"));
        Assert.Empty(await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
            "ls-files", "--others", "--exclude-standard", "-z", "--", "output"));

        var control = Write(root, "control.txt", "before");
        var options = new PipelineOutputDiffOptions();
        if (configuredInclude)
        {
            options.Rules.Add(new() { Action = "Include", Pattern = "ignored*" });
            options.Rules.Add(new() { Action = "Include", Pattern = "control.txt" });
        }
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(options));
        var before = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { control }, before.Files.Keys.ToArray());
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        DeleteLink();
        CreateLink(second);
        Assert.Equal(second, new FileInfo(link).LinkTarget);
        var retargeted = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { control }, retargeted.Files.Keys.ToArray());
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(before, retargeted, TestContext.Current.CancellationToken));
        File.WriteAllText(control, "after!");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal("control.txt", change.Path);

        void CreateLink(string target)
        {
            if (fileLink)
            {
                File.CreateSymbolicLink(link, target);
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }
        }

        void DeleteLink()
        {
            if (fileLink)
            {
                File.Delete(link);
            }
            else
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task SnapshotterCopiesRuleValuesWhenConstructed()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "output/**\n", TestContext.Current.CancellationToken);
        var root = workspace.CreateDirectory("output").FullName;
        var artifact = Write(root, "model.json", "old");
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));
        Assert.Empty(await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken));
        var options = new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem };
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(options));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        options.Mode = PipelineOutputDiffMode.Git;
        options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*" });

        File.WriteAllText(artifact, "new");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));

        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal(Path.GetRelativePath(root, artifact), change.Path);
    }

    [Fact]
    public async Task AutoCaptureUsesGitMembershipWhenWorktreeAppears()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("output").FullName;
        var artifact = Write(root, "model.json", "old");
        Assert.Null(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { artifact }, beforeSnapshot.Files.Keys);

        await InitializeGitAsync(workspace.Path);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "output/**\n", TestContext.Current.CancellationToken);
        File.WriteAllText(artifact, "new");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Empty(afterSnapshot.Files);
        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));

        Assert.Equal(PipelineOutputChangeKind.Removed, change.Kind);
        Assert.Equal(Path.GetRelativePath(root, artifact), change.Path);
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.Auto)]
    [InlineData(PipelineOutputDiffMode.Git)]
    public async Task GitMetadataOutsideConfiguredWorktreeUsesNonWorktreePolicy(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var worktree = workspace.CreateDirectory("worktree").FullName;
        var root = workspace.CreateDirectory("git-storage").FullName;
        // The helper pins GIT_DIR; Windows Git can infer a bare repository from that
        // native path, so request non-bare initialization explicitly.
        await PipelineOutputGitTestHelper.RunAsync(worktree, "init", "--no-bare", "--separate-git-dir", root, worktree);
        await PipelineOutputGitTestHelper.RunAsync(worktree, "config", "--local", "core.worktree", worktree);
        Assert.False(PipelineOutputRoot.IsWithinDirectory(worktree, root));
        var runner = new DefaultProcessRunner();
        var probe = await runner.RunAndCaptureTextAsync("git",
            ["--no-optional-locks", "rev-parse", "--is-inside-work-tree", "--show-cdup"], root,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, probe.ExitCode);
        // Outside a configured worktree, real Git emits "false\n/absolute/worktree\n",
        // not just "false\n". That is a non-worktree result, not malformed discovery.
        Assert.Equal($"false\n{worktree.Replace('\\', '/')}\n", probe.StandardOutput.ReplaceLineEndings("\n"));
        output.WriteLine($"Git discovery stdout: {probe.StandardOutput.ReplaceLineEndings("\n").Replace("\n", "\\n")}");
        var artifact = Write(root, "artifact.txt", "before");
        Assert.Equal("before", File.ReadAllText(artifact));
        var options = new PipelineOutputDiffOptions { Mode = mode };
        // Select only the artifact so Git administration changes cannot affect the diff.
        options.Rules.Add(new() { Action = "Include", Pattern = "artifact.txt" });
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), runner, Options.Create(options));

        if (mode is PipelineOutputDiffMode.Git)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken));
            Assert.Equal($"Git is required to capture pipeline output, but the Git executable or a usable worktree is unavailable: {root}", failure.Message);
            return;
        }

        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        File.WriteAllText(artifact, "after");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        var change = Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.Equal("artifact.txt", change.Path);
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
    }

    [Fact]
    public async Task MissingRootDefersGitDiscoveryUntilGeneration()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "output/**\n", TestContext.Current.CancellationToken);
        var root = Path.Combine(workspace.Path, "output", "new");
        Assert.False(Directory.Exists(root));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Write(root, "ignored.bin");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(root, "ignored.bin")));
    }

    [Fact]
    public async Task WorktreeLossIsRediscoveredButCaptureErrorsAndCancellationPropagate()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        Write(root, "artifact.txt");
        var missingRunner = new PipelineOutputTestProcessRunner(_ => throw new System.ComponentModel.Win32Exception(2));
        Assert.Null(await GitPipelineOutputReader.TryCreateAsync(root, missingRunner, TestContext.Current.CancellationToken));
        Assert.Single(missingRunner.Calls);
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));
        Assert.Single(await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), canceled.Token));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Single(beforeSnapshot.Files);
        Directory.Move(Path.Combine(workspace.Path, ".git"), Path.Combine(workspace.Path, "saved-git"));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".git")));

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken));

        Assert.Contains("Git could not capture pipeline output", failure.Message);
        Assert.Contains("exit code", failure.Message);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(beforeSnapshot.Files.Keys, afterSnapshot.Files.Keys);
        Assert.Equal(PipelineOutputChangeKind.Changed, Assert.Single(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public async Task JsonConfigurationUsesOrdinaryProviderAndPostConfigureOrdering()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Write(workspace.Path, "src/artifacts/model.json");
        Write(workspace.Path, "src/artifacts/cache.tmp");
        Write(workspace.Path, "src/Program.cs");
        Write(workspace.Path, "README.md");
        var special = Write(workspace.Path, "special.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"Pipeline":{"OutputDiff":{"Rules":[
              {"Action":"Exclude","Pattern":"src/**"},
              {"Action":"Include","Pattern":"src/artifacts/**"},
              {"Action":"Exclude","Pattern":"**/*.tmp"}
            ]}}}
            """)));
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"Pipeline":{"OutputDiff":{"Mode":"FileSystem","Rules":[{"Action":"Exclude","Pattern":"**/*"}]}}}
            """)));
        builder.Services.PostConfigure<PipelineOutputDiffOptions>(options => options.Rules.Add(new() { Action = "Include", Pattern = "special.txt" }));
        builder.WithOutputDiffing();
        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<PipelineOutputDiffOptions>>().Value;
        Assert.Equal(PipelineOutputDiffMode.FileSystem, options.Mode);
        Assert.Equal(new[] { "**/*", "src/artifacts/**", "**/*.tmp", "special.txt" }, options.Rules.Select(rule => rule.Pattern));

        var selected = await SelectAsync(options, workspace.Path);

        Assert.Equal(new[] { special, artifact }, selected.Order(PipelineOutputRoot.PathComparer));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public void UndefinedModesAreRejectedAtOptionsResolution(int value, bool throughConfiguration)
    {
        var mode = (PipelineOutputDiffMode)value;
        Assert.False(Enum.IsDefined(mode));
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        if (throughConfiguration)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pipeline:OutputDiff:Mode"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        else
        {
            builder.Services.Configure<PipelineOutputDiffOptions>(options => options.Mode = mode);
        }
        builder.WithOutputDiffing();
        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<PipelineOutputDiffOptions>>();

        var failure = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.Contains("mode", failure.Message);
    }

    [Theory]
    [InlineData("""{"Pattern":"**/*"}""")]
    [InlineData("""{"Action":"","Pattern":"**/*"}""")]
    [InlineData("""{"Action":null,"Pattern":"**/*"}""")]
    [InlineData("""{"Action":"Drop","Pattern":"**/*"}""")]
    [InlineData("""{"Action":1,"Pattern":"**/*"}""")]
    [InlineData("""{"Action":"Include"}""")]
    [InlineData("""{"Action":"Include","Pattern":null}""")]
    [InlineData("""{"Action":"Include","Pattern":""}""")]
    [InlineData("""{"Action":"Include","Pattern":"  "}""")]
    [InlineData("""{"Action":"Include","Pattern":"/absolute/**"}""")]
    [InlineData("""{"Action":"Include","Pattern":"../outside/**"}""")]
    [InlineData("""{"Action":"Include","Pattern":"inside/../outside/**"}""")]
    [InlineData("""{"Action":"Include","Pattern":"bad\u0000pattern"}""")]
    [InlineData("null")]
    public async Task MalformedJsonRulesAreRejectedAtOptionsResolution(string rule)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Pipeline":{"OutputDiff":{"Mode":"FileSystem","Rules":[""" + rule + "]}}}")));
        builder.WithOutputDiffing();
        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<PipelineOutputDiffOptions>>();
        Assert.Throws<OptionsValidationException>(() => options.Value);

    }

    [Fact]
    public async Task GlobCaseUsesPlatformPathComparison()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Write(workspace.Path, "artifact.TMP");
        var options = new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem };
        options.Rules.Add(new() { Action = "Include", Pattern = "**/*" });
        options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*.tmp" });
        Assert.True(File.Exists(path));

        var selected = await SelectAsync(options, workspace.Path);

        Assert.Equal(OperatingSystem.IsWindows() ? Array.Empty<string>() : new[] { path }, selected);
    }

    [Theory]
    [InlineData("link", true)]
    [InlineData("link/**", false)]
    [InlineData("**/*", true)]
    public async Task ExplicitRulesCompareDirectoryLinksWithoutTraversingThem(string pattern, bool includeLink)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("output").FullName;
        var link = Path.Combine(root, "link");
        Write(root, "artifact.txt");
        Directory.CreateSymbolicLink(link, root);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        var options = new PipelineOutputDiffOptions { Mode = PipelineOutputDiffMode.FileSystem };
        options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*" });
        options.Rules.Add(new() { Action = "Include", Pattern = pattern });

        var paths = await SelectAsync(options, root);

        var expected = pattern == "**/*" ? new[] { Path.Combine(root, "artifact.txt"), link } :
            includeLink ? new[] { link } : Array.Empty<string>();
        Assert.Equal(expected, paths.Order(PipelineOutputRoot.PathComparer));
    }

    [Fact]
    public async Task GitPathsPreserveWhitespaceUnicodeAndNulDelimiting()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var paths = new List<string> { Write(root, " leading space .json"), Write(root, "日本語 ü.json") };
        if (!OperatingSystem.IsWindows())
        {
            paths.Add(Write(root, "line\nname.json"));
            paths.Add(Write(root, " \t "));
        }
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));

        var selected = (await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken)).Select(entry => entry.File.FullName);

        Assert.Equal(paths.Order(PipelineOutputRoot.PathComparer), selected.Order(PipelineOutputRoot.PathComparer));
    }

    [Fact]
    public async Task GitStreamingBatchesExceedPipeBuffersAndHonorNegations()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        Write(root, ".gitignore", "*.tmp\n!keep*.tmp\n\\!literal.json\nignored/\n");
        var expected = new List<string> { Path.Combine(root, ".gitignore") };
        // Both request and verbose response streams exceed pipe buffers in one breadth level.
        for (var i = 0; i < 1200; i++)
        {
            expected.Add(Write(root, $"keep-{i:D4}-日本語-xxxxxxxxxxxxxxxxxxxxxxxx.tmp"));
            Write(root, $"drop-{i:D4}-xxxxxxxxxxxxxxxxxxxxxxxx.tmp");
        }
        Write(root, "!literal.json");
        Write(root, "ignored/child/hidden.json");
        expected.Add(Write(root, "nested/keep.tmp"));
        expected.Add(Write(root, "nested/plain.json"));
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));

        var selected = (await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken)).Select(entry => entry.File.FullName);

        Assert.Equal(expected.Order(PipelineOutputRoot.PathComparer), selected.Order(PipelineOutputRoot.PathComparer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitIndexedLeavesBypassIgnoresButDirectoryReplacementsStillPrune(bool replaceWithLink)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        Write(workspace.Path, ".gitignore", "*.tmp\n");
        var root = workspace.CreateDirectory("output").FullName;
        var artifact = Write(root, "artifact.tmp");
        var visible = Write(root, "visible.json");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "-f", "--", "output/artifact.tmp", "output/visible.json");
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(root, new DefaultProcessRunner(), TestContext.Current.CancellationToken));
        // Both discovered leaves are indexed, so the first capture has no ignore input at all.
        Assert.Equal(new[] { artifact, visible }, (await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken)).Select(entry => entry.File.FullName).Order(PipelineOutputRoot.PathComparer));
        File.Delete(artifact);
        if (replaceWithLink)
        {
            // A real symlink remains a Git leaf on Windows too; a junction represents a directory.
            var target = Write(workspace.Path, "external-target.json");
            File.CreateSymbolicLink(artifact, target);
            Assert.True((File.GetAttributes(artifact) & FileAttributes.ReparsePoint) != 0);
        }
        else
        {
            Write(root, "artifact.tmp/must-not-walk.json");
            Assert.True((File.GetAttributes(artifact) & FileAttributes.Directory) != 0);
        }

        var selected = (await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken)).Select(entry => entry.File.FullName);

        Assert.Equal(replaceWithLink ? new[] { artifact, visible } : new[] { visible }, selected.Order(PipelineOutputRoot.PathComparer));
    }

    [Fact]
    public async Task GitWholeIndexFromNestedWorkingDirectoryStaysInsideComparisonRoot()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var sibling = Write(workspace.Path, "output-sibling/outside.json");
        var outer = Write(workspace.Path, "outside.json");
        var inside = Write(root, "nested/artifact.json");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", ".");
        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(outer));
        var git = Assert.IsType<GitPipelineOutputReader>(await GitPipelineOutputReader.TryCreateAsync(Path.Combine(root, "nested"), new DefaultProcessRunner(), TestContext.Current.CancellationToken));

        Assert.Equal(new[] { inside }, (await git.SelectFilesAsync(root, PipelineOutputGitTestHelper.AllFilesMatcher(), TestContext.Current.CancellationToken)).Select(entry => entry.File.FullName));
    }

    [Fact]
    public async Task PublicRawDirectoryLinksCompareTargetsRatherThanTargetContentsEvenInGit()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var first = workspace.CreateDirectory("first-target").FullName;
        var second = workspace.CreateDirectory("second-target").FullName;
        Write(first, "model.txt", "same content");
        Write(second, "model.txt", "same content");
        var link = Path.Combine(root, "link");
        await PipelineOutputGitTestHelper.CreateDirectoryLinkAsync(link, first, junction: OperatingSystem.IsWindows());
        Assert.Equal(first, new DirectoryInfo(link).LinkTarget);
        var snapshotter = new PipelineOutputSnapshotter();
        var before = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Directory.Delete(link);
        await PipelineOutputGitTestHelper.CreateDirectoryLinkAsync(link, second, junction: OperatingSystem.IsWindows());
        Assert.Equal(second, new DirectoryInfo(link).LinkTarget);
        var after = await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = DefaultPipelineOutputComparer.Compare(before, after, root);

        var change = Assert.Single(changes);
        Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
        Assert.Equal(Path.GetRelativePath(root, link), change.Path);
        Assert.Equal(new[] { link }, (await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken)).Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
        Directory.Delete(link);
        await PipelineOutputGitTestHelper.CreateDirectoryLinkAsync(link, root, junction: OperatingSystem.IsWindows());

        Assert.Equal(new[] { link }, (await snapshotter.CaptureAsync(root, TestContext.Current.CancellationToken)).Keys.Order(PipelineOutputRoot.PathComparer).ToArray());
    }

    [Fact]
    public async Task GitTrackedDirectoryReplacementCannotCaptureLinkedDescendants()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var original = workspace.CreateDirectory("output/child").FullName;
        var external = workspace.CreateDirectory("external").FullName;
        var tracked = Write(original, "model.txt");
        Write(external, "model.txt");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/child/model.txt");
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { tracked }, await SelectAsync(new(), root));

        Directory.Delete(original, recursive: true);
        Directory.CreateSymbolicLink(original, external);
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);

        Assert.Collection(changes,
            change => { Assert.Equal(PipelineOutputChangeKind.Added, change.Kind); Assert.Equal(Path.GetRelativePath(root, original), change.Path); },
            change => { Assert.Equal(PipelineOutputChangeKind.Removed, change.Kind); Assert.Equal(Path.GetRelativePath(root, tracked), change.Path); });
        Assert.Equal(new[] { original }, await SelectAsync(new(), root));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("split")]
    [InlineData("skip-worktree")]
    [InlineData("sparse")]
    [InlineData("unmerged")]
    public async Task GitTrackedIgnoredFilesAndDescendantsRemainIncluded(string indexKind)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "output/*.tmp\noutput/ignored/\n", TestContext.Current.CancellationToken);
        var direct = Write(root, "model.tmp", "old");
        var tracked = Write(root, "ignored/deep/model.json", "old");
        var visible = Write(root, "visible.json");
        Write(root, "cache.tmp");
        Write(root, "ignored/deep/cache.bin");
        string[] trackedPaths = ["output/model.tmp", "output/ignored/deep/model.json"];
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, ["add", "-f", "--", .. trackedPaths]);
        switch (indexKind)
        {
            case "split":
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", "--split-index");
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(workspace.Path, ".git"), "sharedindex.*"));
                break;
            case "skip-worktree":
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, ["update-index", "--skip-worktree", "--", .. trackedPaths]);
                Assert.Equal(trackedPaths.Select(path => "S " + path).Order(),
                    (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "-t", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Order());
                break;
            case "sparse":
                Write(workspace.Path, "excluded/model.json");
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "excluded/model.json");
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
                    "-c", "user.name=Pipeline Output Test", "-c", "user.email=pipeline-output@example.invalid",
                    "-c", "core.hooksPath=" + Path.Combine(workspace.Path, ".git", "no-hooks"),
                    "commit", "--no-gpg-sign", "-m", "Initial output");
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "sparse-checkout", "set", "--cone", "--sparse-index", "output");
                Assert.False(Directory.Exists(Path.Combine(workspace.Path, "excluded")));
                Assert.Equal("040000", (await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
                    "ls-files", "--sparse", "--format=%(objectmode)", "--", "excluded/")).Trim());
                break;
            case "unmerged":
                var trees = new List<string>();
                foreach (var content in new[] { "base", "ours", "theirs" })
                {
                    File.WriteAllText(direct, content);
                    File.WriteAllText(tracked, content);
                    await PipelineOutputGitTestHelper.RunAsync(workspace.Path, ["add", "-f", "--", .. trackedPaths]);
                    trees.Add((await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "write-tree")).Trim());
                }
                // Merge only the fixture's index; leave real files available to the output capture.
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "read-tree", trees[1]);
                await PipelineOutputGitTestHelper.RunAsync(workspace.Path, ["read-tree", "-i", "-m", .. trees]);
                Assert.Equal(new[] { "1", "1", "2", "2", "3", "3" },
                    (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "--unmerged", "--format=%(stage)", "-z"))
                        .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order());
                break;
        }
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        Assert.Equal(new[] { tracked, direct, visible }.Order(PipelineOutputRoot.PathComparer),
            (await SelectAsync(new(), root)).Order(PipelineOutputRoot.PathComparer));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        File.WriteAllText(direct, "new");
        File.WriteAllText(tracked, "new");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { tracked, direct }.Select(path => Path.GetRelativePath(root, path)).Order(PipelineOutputRoot.PathComparer), changes.Select(change => change.Path));
        Assert.All(changes, change => Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitMissingSkipWorktreeIgnoreFilesApplyToUntrackedOutput(bool sparseCheckout)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var ignorePath = sparseCheckout ? "output/cache/.gitignore" : "output/.gitignore";
        var ignore = Write(workspace.Path, ignorePath, "*.tmp\n");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", ignorePath);
        if (sparseCheckout)
        {
            Write(workspace.Path, "keep/model.json");
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "keep/model.json");
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
                "-c", "user.name=Pipeline Output Test", "-c", "user.email=pipeline-output@example.invalid",
                "-c", "core.hooksPath=" + Path.Combine(workspace.Path, ".git", "no-hooks"),
                "commit", "--no-gpg-sign", "-m", "Initial output");
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "sparse-checkout", "set", "--cone", "--sparse-index", "keep");
        }
        else
        {
            // An ancestor outside the comparison root can supply indexed ignores without sparse checkout.
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.sparseCheckout", "false");
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", "--skip-worktree", "--", ignorePath);
            File.Delete(ignore);
            Assert.Equal("false", (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "--bool", "core.sparseCheckout")).Trim());
        }
        Assert.False(File.Exists(ignore));
        Assert.Equal("S " + ignorePath + '\0', await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "ls-files", "-t", "-z", "--", ignorePath));
        var root = workspace.CreateDirectory(sparseCheckout ? "output" : "output/cache").FullName;
        var cache = workspace.CreateDirectory("output/cache").FullName;
        var visible = Write(cache, "visible.json");
        var removed = Write(cache, "old.tmp");
        var added = Path.Combine(cache, "new.tmp");
        Assert.True(File.Exists(removed));
        Assert.False(File.Exists(added));

        Assert.Equal(new[] { visible }, await SelectAsync(new(), root));
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        File.Delete(removed);
        Write(cache, "new.tmp");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(removed));
        Assert.True(File.Exists(added));
        Assert.Equal(new[] { visible }, await SelectAsync(new(), root));
        Assert.Empty(await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GitNestedRepositoriesPreserveEnclosingRepositoryTrackedArtifacts(bool validRepository, bool trackedByEnclosingRepository)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var nested = workspace.CreateDirectory("output/nested").FullName;
        var artifact = Write(nested, "model.json", "old");
        if (trackedByEnclosingRepository)
        {
            await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "output/nested/model.json");
        }
        if (validRepository)
        {
            await InitializeGitAsync(nested);
        }
        else
        {
            File.WriteAllText(Path.Combine(nested, ".git"), "not a repository");
        }
        Assert.Equal("old", File.ReadAllText(artifact));
        if (trackedByEnclosingRepository)
        {
            var ordinaryGitSelection = await PipelineOutputGitTestHelper.RunAsync(workspace.Path,
                "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "output");
            Assert.Contains("output/nested/model.json", ordinaryGitSelection.Split('\0'));
        }
        var expected = !validRepository || trackedByEnclosingRepository ? new[] { artifact } : Array.Empty<string>();
        var paths = await SelectAsync(new(), root);
        var nativeSnapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(new PipelineOutputDiffOptions()));
        var beforeSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);

        File.WriteAllText(artifact, "new");
        var afterSnapshot = await nativeSnapshotter.CaptureAsync(root, TestContext.Current.CancellationToken);
        var changes = await new DefaultPipelineOutputComparer().CompareAsync(beforeSnapshot, afterSnapshot, TestContext.Current.CancellationToken);

        Assert.Equal(expected, paths);
        if (expected.Length == 0)
        {
            Assert.Empty(changes);
        }
        else
        {
            var change = Assert.Single(changes);
            Assert.Equal(PipelineOutputChangeKind.Changed, change.Kind);
            Assert.Equal(Path.GetRelativePath(root, artifact), change.Path);
        }
    }

    [Fact]
    public async Task GitSkipsSubmoduleContentsEvenWithoutAnAdministrationFile()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await InitializeGitAsync(workspace.Path);
        var root = workspace.CreateDirectory("output").FullName;
        var nested = workspace.CreateDirectory("output/module").FullName;
        var artifact = Write(nested, "model.json");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "update-index", "--add", "--cacheinfo",
            "160000," + new string('1', 40) + ",output/module");
        Assert.True(File.Exists(artifact));
        Assert.False(File.Exists(Path.Combine(nested, ".git")));

        var paths = await SelectAsync(new(), root);

        Assert.Empty(paths);

        Directory.Delete(nested, recursive: true);
        var target = workspace.CreateDirectory("external-module").FullName;
        Write(target, "model.json");
        Directory.CreateSymbolicLink(nested, target);

        // Git itself rejects this gitlink-to-symlink state. Keep its failure rather than
        // importing linked contents or silently replacing the selected Git backend.
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => SelectAsync(new(), root));
        Assert.Contains("submodule", failure.Message);
    }

    private static async Task<IReadOnlyList<string>> SelectAsync(PipelineOutputDiffOptions options, string directory)
    {
        var snapshotter = new DefaultPipelineOutputSnapshotter(new(), new DefaultProcessRunner(), Options.Create(options));
        return (await snapshotter.CaptureAsync(directory, TestContext.Current.CancellationToken)).Files.Keys.Order(PipelineOutputRoot.PathComparer).ToArray();
    }

    private static async Task InitializeGitAsync(string directory)
    {
        await PipelineOutputGitTestHelper.RunAsync(directory, "init");
        await PipelineOutputGitTestHelper.RunAsync(directory, "config", "core.excludesFile", Path.Combine(directory, ".git", "info", "exclude"));
    }

    private static string Write(string root, string relative, string content = "content")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}

#pragma warning restore ASPIREPIPELINES005
