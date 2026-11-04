// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

public class PipelineOutputDiffingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiffServicesAndConfigurationAreOptIn(bool enabled)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:OutputDiff:Mode"] = "FileSystem",
            ["Pipeline:OutputDiff:Rules:0:Action"] = "Include",
            ["Pipeline:OutputDiff:Rules:0:Pattern"] = "generated/**"
        });
        if (enabled)
        {
            builder.WithOutputDiffing();
        }
        using var app = builder.Build();
        var snapshotter = app.Services.GetService<IPipelineOutputSnapshotter<PipelineOutputSnapshot>>();
        var comparer = app.Services.GetService<IPipelineOutputComparer<PipelineOutputSnapshot>>();
        var options = app.Services.GetRequiredService<IOptions<PipelineOutputDiffOptions>>().Value;

        if (enabled)
        {
            Assert.IsType<DefaultPipelineOutputSnapshotter>(snapshotter);
            Assert.IsType<DefaultPipelineOutputComparer>(comparer);
            Assert.Equal(PipelineOutputDiffMode.FileSystem, options.Mode);
            Assert.Equal("generated/**", Assert.Single(options.Rules).Pattern);
        }
        else
        {
            Assert.Null(snapshotter);
            Assert.Null(comparer);
            Assert.Equal(PipelineOutputDiffMode.Auto, options.Mode);
            Assert.Empty(options.Rules);
        }
    }

    [Fact]
    public void MultipleChecksBindConfigurationOnceAndPreserveConfigureCallbacks()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:OutputDiff:Mode"] = "Git",
            ["Pipeline:OutputDiff:Rules:0:Action"] = "Include",
            ["Pipeline:OutputDiff:Rules:0:Pattern"] = "generated/**"
        });
        builder.WithOutputDiffing();
        builder.Services.Configure<PipelineOutputDiffOptions>(options =>
        {
            options.Mode = PipelineOutputDiffMode.FileSystem;
            options.Rules[0].Pattern = "artifacts/**";
        });
        builder.WithOutputDiffing(diffStepName: "second-check");
        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<PipelineOutputDiffOptions>>().Value;

        Assert.Equal(PipelineOutputDiffMode.FileSystem, options.Mode);
        Assert.Equal("artifacts/**", Assert.Single(options.Rules).Pattern);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RegisteredFileHasherDefinesTextAndMetadataEquivalence(bool changeContent, bool changeMetadata)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var generated = workspace.CreateDirectory("generated").FullName;
        var artifact = Path.Combine(generated, "deployment.yaml");
        await File.WriteAllTextAsync(artifact, "services: [api]\r\n", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Services.Configure<PipelineOutputDiffOptions>(options =>
        {
            options.Mode = PipelineOutputDiffMode.FileSystem;
            options.Rules.Add(new() { Action = "Include", Pattern = "generated/**" });
        });
        builder.Services.PostConfigure<PipelineOutputDiffOptions>(options =>
            options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*.tmp" }));
        var calls = 0;
        var hasher = new TestPipelineOutputFileHasher(async (entry, ct) =>
        {
            var file = Assert.IsType<FileInfo>(entry);
            Interlocked.Increment(ref calls);
            Assert.Equal(artifact, file.FullName);
            Assert.True(file.Exists);
            Assert.Equal(new FileInfo(artifact).Length, file.Length);
            Assert.Equal(File.GetAttributes(artifact), file.Attributes);
            using var reader = File.OpenText(file.FullName);
            var content = await reader.ReadToEndAsync(ct);
            var readOnly = (file.Attributes & FileAttributes.ReadOnly) != 0;
            return XxHash3.Hash(Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n") + readOnly));
        });
        builder.Services.AddSingleton<IPipelineOutputFileHasher>(hasher);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            await File.WriteAllTextAsync(artifact, changeContent ? "services: [other]\n" : "services: [api]\n", TestContext.Current.CancellationToken);
            if (changeMetadata)
            {
                File.SetAttributes(artifact, File.GetAttributes(artifact) | FileAttributes.ReadOnly);
            }
        });
        using var app = builder.Build();
        Assert.Equal(0, calls);
        Assert.Equal("services: [api]\r\n", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        Assert.Equal(0, (int)(File.GetAttributes(artifact) & FileAttributes.ReadOnly));
        try
        {
            if (changeContent || changeMetadata)
            {
                var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
                Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.Combine("generated", "deployment.yaml"), failure.Message);
            }
            else
            {
                await ExecuteAsync(app);
            }
            Assert.Equal(changeMetadata, (File.GetAttributes(artifact) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            File.SetAttributes(artifact, File.GetAttributes(artifact) & ~FileAttributes.ReadOnly);
        }
        Assert.Equal(2, calls);
        Assert.Equal(changeContent ? "services: [other]\n" : "services: [api]\n", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(257, false)]
    [InlineData(257, true)]
    [InlineData(81920, false)]
    [InlineData(81920, true)]
    [InlineData(81921, false)]
    [InlineData(81921, true)]
    [InlineData(1048593, false)]
    [InlineData(1048593, true)]
    public async Task RegisteredByteFileHasherStreamsBinaryOutputAndDisposesStreams(int length, bool changed)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "artifact.bin");
        var control = Path.Combine(workspace.Path, "control.bin");
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(artifact, bytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(control, [0, 255, 13, 10, 128], TestContext.Current.CancellationToken);
        var generated = bytes.ToArray();
        if (changed)
        {
            if (length == 0)
            {
                generated = [255];
            }
            else
            {
                generated[^1] ^= 255;
            }
            Assert.False(bytes.SequenceEqual(generated));
        }
        else
        {
            Assert.Equal(bytes, generated);
        }
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Services.AddSingleton<IPipelineOutputFileHasher, ByteOutputFileHasher>();
        builder.Services.Configure<PipelineOutputDiffOptions>(options => options.Mode = PipelineOutputDiffMode.FileSystem);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, _ =>
            File.WriteAllBytesAsync(artifact, generated, TestContext.Current.CancellationToken));
        using var app = builder.Build();
        var hasher = Assert.IsType<ByteOutputFileHasher>(app.Services.GetRequiredService<IPipelineOutputFileHasher>());
        Assert.Empty(hasher.Streams);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact, TestContext.Current.CancellationToken));

        if (changed)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: artifact.bin", failure.Message);
        }
        else
        {
            await ExecuteAsync(app);
        }

        Assert.Equal(generated, await File.ReadAllBytesAsync(artifact, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, await File.ReadAllBytesAsync(control, TestContext.Current.CancellationToken));
        Assert.Equal(4, hasher.Streams.Count);
        Assert.All(hasher.Streams, stream => Assert.False(stream.CanRead));
        using var exclusive = new FileStream(artifact, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(generated.Length, exclusive.Length);
    }

    [Fact]
    public async Task BaselinePrecedesWriterWithoutPublishPrerequisite()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "early.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotter = new RecordingSnapshotter(artifact);
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.Pipeline.AddStep("unordered-writer", _ =>
        {
            Assert.Equal(new[] { "capture" }, snapshotter.Events);
            return File.WriteAllTextAsync(artifact, "written after baseline", TestContext.Current.CancellationToken);
        }, requiredBy: WellKnownPipelineSteps.Publish);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>();
        using var app = builder.Build();
        Assert.Empty(snapshotter.Events);
        Assert.False(File.Exists(artifact));

        await ExecuteAsync(app);

        Assert.Equal(new[] { "capture", "capture", "compare" }, snapshotter.Events);
        Assert.Equal("written after baseline", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SimpleAndFactoryChecksPreserveMetadataAndIncludeTheirActions(bool useFactory, bool useSequential)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "factory.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "check-alias");
        var resource = builder.AddResource(new OutputResource("files"));
        builder.Pipeline.AddStep("custom-target", _ => Task.CompletedTask);
        builder.Pipeline.AddStep("check-alias", _ => Task.CompletedTask);
        Func<PipelineStepContext, Task> action = _ => File.WriteAllTextAsync(artifact, "factory action", TestContext.Current.CancellationToken);
        if (useFactory)
        {
            builder.WithOutputDiffing(() => new PipelineStep
            {
                Name = "custom-check",
                Description = "Check custom output",
                Action = action,
                DependsOnSteps = ["custom-target"],
                RequiredBySteps = ["check-alias"],
                Tags = ["custom-tag"],
                Resource = resource.Resource
            });
        }
        else
        {
            builder.WithOutputDiffing("custom-target", "custom-check", "Check custom output");
            builder.Pipeline.WithFinalAction("custom-target", action);
            builder.Pipeline.AddStep("select-check", _ => Task.CompletedTask, dependsOn: "custom-check", requiredBy: "check-alias");
        }
        using var app = builder.Build();
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken);
        var steps = await pipeline.ResolveStepsAsync(context);
        var check = Assert.Single(steps, step => step.Name == "custom-check");
        Assert.Equal("Check custom output", check.Description);
        Assert.Contains("custom-target", check.DependsOnSteps);
        Assert.Contains(steps, step => step.Name == "custom-check-baseline");
        if (useFactory)
        {
            Assert.Same(action, check.Action);
            Assert.Contains("custom-tag", check.Tags);
            Assert.Contains("check-alias", check.RequiredBySteps);
            Assert.Same(resource.Resource, check.Resource);
        }
        Assert.False(File.Exists(artifact));

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => useSequential
            ? pipeline.ExecuteStepSequentiallyAsync("check-alias", context)
            : ExecuteAsync(app));

        Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Added: factory.txt", failure.Message);
        Assert.Equal("factory action", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InspectionDoesNotResolveCaptureServices()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Services.AddTransient<IPipelineOutputSnapshotter<PipelineOutputSnapshot>>(_ => throw new InvalidOperationException("typed snapshotter resolved"));
        builder.Services.AddTransient<IPipelineOutputComparer<PipelineOutputSnapshot>>(_ => throw new InvalidOperationException("comparer resolved"));
        builder.Services.AddSingleton<PipelineOutputSnapshotter>(_ => throw new InvalidOperationException("snapshotter resolved"));
        builder.Services.AddSingleton<IPipelineOutputService>(_ => throw new InvalidOperationException("output resolved"));
        builder.WithOutputDiffing();
        using var app = builder.Build();
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken);

        var steps = await pipeline.ResolveStepsAsync(context);

        Assert.Contains(steps, step => step.Name == "publish-and-diff");
        Assert.Empty(Assert.Single(steps, step => step.Name == "publish-and-diff-baseline").DependsOnSteps);
    }

    [Fact]
    public async Task LateResourceConfigurationCannotRemoveBaselineOrdering()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "late.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotter = new RecordingSnapshotter(artifact);
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>();
        builder.AddResource(new OutputResource("late-writer"))
            .WithPipelineStepFactory(_ => new PipelineStep
            {
                Name = "late-writer",
                Action = _ =>
                {
                    Assert.Equal(new[] { "capture" }, snapshotter.Events);
                    return File.WriteAllTextAsync(artifact, "late output", TestContext.Current.CancellationToken);
                },
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq]
            })
            .WithPipelineConfiguration(context =>
            {
                var writer = Assert.Single(context.Steps, step => step.Name == "late-writer");
                Assert.Contains(WellKnownPipelineSteps.PublishPrereq, writer.DependsOnSteps);
                writer.DependsOnSteps.Clear();
                writer.RequiredBy(WellKnownPipelineSteps.Publish);
            });
        using var app = builder.Build();
        Assert.Empty(snapshotter.Events);
        Assert.False(File.Exists(artifact));

        await ExecuteAsync(app);

        Assert.Equal(new[] { "capture", "capture", "compare" }, snapshotter.Events);
        Assert.Equal("late output", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OrdinaryTargetDoesNotResolveAnyDiffServices()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        builder.Services.AddTransient<IPipelineOutputSnapshotter<PipelineOutputSnapshot>>(_ => throw new InvalidOperationException("typed snapshotter resolved"));
        builder.Services.AddTransient<IPipelineOutputComparer<PipelineOutputSnapshot>>(_ => throw new InvalidOperationException("comparer resolved"));
        builder.Services.AddSingleton<PipelineOutputSnapshotter>(_ => throw new InvalidOperationException("snapshotter resolved"));
        builder.Services.AddSingleton<IPipelineOutputService>(_ => throw new InvalidOperationException("output resolved"));
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        var artifact = Path.Combine(workspace.Path, "ordinary.txt");
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, _ => File.WriteAllTextAsync(artifact, "ordinary output", TestContext.Current.CancellationToken));
        using var app = builder.Build();
        Assert.False(File.Exists(artifact));

        await ExecuteAsync(app);

        Assert.Equal("ordinary output", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshGitCheckoutIgnoresBuildOutputsButChecksTrackedArtifacts(bool changeArtifact)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "tracked.tmp");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "**/bin/\n**/obj/\n*.tmp\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(artifact, "indexed", TestContext.Current.CancellationToken);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.excludesFile", Path.Combine(workspace.Path, ".git", "info", "exclude"));
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "-f", "--", ".gitignore", "tracked.tmp");
        await File.WriteAllTextAsync(artifact, "already dirty", TestContext.Current.CancellationToken);
        Assert.Contains("tracked.tmp", await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "diff", "--name-only"));
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "src", "Api", "bin")));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "src", "Api", "obj")));
            var binaryDirectory = workspace.CreateDirectory("src/Api/bin/Release");
            var objectDirectory = workspace.CreateDirectory("src/Api/obj");
            await File.WriteAllTextAsync(Path.Combine(binaryDirectory.FullName, "Api.dll"), "build output", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(objectDirectory.FullName, "project.assets.json"), "{}", TestContext.Current.CancellationToken);
            if (changeArtifact)
            {
                await File.WriteAllTextAsync(artifact, "generated change", TestContext.Current.CancellationToken);
            }
        });
        using var app = builder.Build();
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "src")));

        if (changeArtifact)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);
        }
        else
        {
            await ExecuteAsync(app);
        }

        Assert.Equal(changeArtifact ? "generated change" : "already dirty", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "Api", "bin", "Release", "Api.dll")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "src", "Api", "obj", "project.assets.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultPolicyChecksGenerationOnlyWithUnchangedSource(bool staleArtifact)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceDirectory = workspace.CreateDirectory("src/Api").FullName;
        var source = Path.Combine(sourceDirectory, "Program.cs");
        var artifact = Path.Combine(workspace.CreateDirectory("generated").FullName, "deployment.yaml");
        const string sourceText = "var builder = WebApplication.CreateBuilder(args);";
        const string generatedText = "services: [api]";
        await File.WriteAllTextAsync(source, sourceText, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(artifact, staleArtifact ? "services: [old-api]" : generatedText, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "**/bin/\n**/obj/\n", TestContext.Current.CancellationToken);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.excludesFile", Path.Combine(workspace.Path, ".git", "info", "exclude"));
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", ".gitignore", "src", "generated");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        var generationCalls = 0;
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            Assert.Equal(sourceText, await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "bin")));
            Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "obj")));
            generationCalls++;
            await File.WriteAllTextAsync(artifact, generatedText, TestContext.Current.CancellationToken);
        });
        using var app = builder.Build();
        Assert.Equal(0, generationCalls);
        Assert.Equal(staleArtifact ? "services: [old-api]" : generatedText, await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        if (staleArtifact)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);
        }
        else
        {
            await ExecuteAsync(app);
        }

        Assert.Equal(1, generationCalls);
        Assert.Equal(generatedText, await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.Equal(sourceText, await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "bin")));
        Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "obj")));
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("keys", false)]
    [InlineData("keys", true)]
    [InlineData("options", false)]
    [InlineData("options", true)]
    public async Task ConfiguredPositiveScopeChecksArtifactsButNotSource(string configuration, bool changeArtifact)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.CreateDirectory("src").FullName, "Program.cs");
        var generated = workspace.CreateDirectory("generated").FullName;
        var artifact = Path.Combine(generated, "deployment.yaml");
        var temporary = Path.Combine(generated, "cache.tmp");
        foreach (var path in new[] { source, artifact, temporary })
        {
            await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        }
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"Pipeline":{"OutputDiff":{"Mode":"FileSystem","Rules":[
              {"Action":"Include","Pattern":"src/**"}
            ]}}}
            """)));
        builder.WithOutputDiffing();
        if (configuration == "json")
        {
            builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
                {"Pipeline":{"OutputDiff":{"Rules":[
                  {"Action":"Include","Pattern":"generated/**"},
                  {"Action":"Exclude","Pattern":"**/*.tmp"}
                ]}}}
                """)));
        }
        else if (configuration == "keys")
        {
            // Normalized environment-provider keys override one array entry, not the whole array.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pipeline:OutputDiff:Rules:0:Pattern"] = "generated/**",
                ["Pipeline:OutputDiff:Rules:1:Action"] = "Exclude",
                ["Pipeline:OutputDiff:Rules:1:Pattern"] = "**/*.tmp"
            });
        }
        else if (configuration == "options")
        {
            builder.Services.Configure<PipelineOutputDiffOptions>(options => options.Rules[0].Pattern = "generated/**");
            builder.Services.PostConfigure<PipelineOutputDiffOptions>(options => options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*.tmp" }));
        }
        AddArtifactWriter(builder.Pipeline);
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            await File.WriteAllTextAsync(source, "new", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(temporary, "new", TestContext.Current.CancellationToken);
            if (changeArtifact)
            {
                await File.WriteAllTextAsync(artifact, "new", TestContext.Current.CancellationToken);
            }
        });
        using var app = builder.Build();
        Assert.Equal("old", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal("old", await File.ReadAllTextAsync(temporary, TestContext.Current.CancellationToken));
        Assert.Equal("old", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        if (changeArtifact)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);
        }
        else
        {
            await ExecuteAsync(app);
        }

        Assert.Equal("new", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal("new", await File.ReadAllTextAsync(temporary, TestContext.Current.CancellationToken));
        Assert.Equal(changeArtifact ? "new" : "old", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(PipelineOutputDiffMode.Auto)]
    [InlineData(PipelineOutputDiffMode.Git)]
    [InlineData(PipelineOutputDiffMode.FileSystem)]
    public async Task ConfiguredIncludeOfIgnoredArtifactRequiresGitOptOut(PipelineOutputDiffMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.CreateDirectory("generated").FullName, "deployment.yaml");
        await File.WriteAllTextAsync(artifact, "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "generated/\n", TestContext.Current.CancellationToken);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.excludesFile", Path.Combine(workspace.Path, ".git", "info", "exclude"));
        Assert.Equal("generated/deployment.yaml", (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "check-ignore", "--", "generated/deployment.yaml")).Trim());
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:OutputDiff:Mode"] = mode.ToString(),
            ["Pipeline:OutputDiff:Rules:0:Action"] = "Include",
            ["Pipeline:OutputDiff:Rules:0:Pattern"] = "generated/**"
        });
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, _ => File.WriteAllTextAsync(artifact, "new", TestContext.Current.CancellationToken));
        using var app = builder.Build();
        Assert.Equal("old", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        if (mode is PipelineOutputDiffMode.Auto or PipelineOutputDiffMode.Git)
        {
            await ExecuteAsync(app);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);
        }

        Assert.Equal("new", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesystemModeIncludesIgnoredArtifactsAndDetectsRawEolChanges(bool changeContent)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.CreateDirectory("generated").FullName, "deployment.yaml");
        const string before = "services: [api]\n";
        var after = changeContent ? "services: [other-api]\r\n" : "services: [api]\r\n";
        await File.WriteAllTextAsync(artifact, before, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitattributes"), "*.yaml text eol=lf\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, ".gitignore"), "generated/\n", TestContext.Current.CancellationToken);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.autocrlf", "false");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.safecrlf", "false");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "config", "core.excludesFile", Path.Combine(workspace.Path, ".git", "info", "exclude"));
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", ".gitattributes", ".gitignore");
        Assert.Equal("generated/deployment.yaml", (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "check-ignore", "--", "generated/deployment.yaml")).Trim());
        var beforeObject = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "hash-object", "--", artifact);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:OutputDiff:Mode"] = "FileSystem",
            ["Pipeline:OutputDiff:Rules:0:Action"] = "Include",
            ["Pipeline:OutputDiff:Rules:0:Pattern"] = "generated/**"
        });
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, _ => File.WriteAllTextAsync(artifact, after, TestContext.Current.CancellationToken));
        using var app = builder.Build();
        Assert.Equal(before, await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.NotEqual(before, after);

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);

        Assert.Equal(after, await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        var afterObject = await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "hash-object", "--", artifact);
        Assert.Equal(!changeContent, beforeObject == afterObject);
    }

    [Fact]
    public async Task CustomProviderOwnsPolicyWithoutGitOrValidNativeOptions()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "stable.txt");
        await File.WriteAllTextAsync(artifact, "stable", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pipeline:OutputDiff:Rules:0:Action"] = "CustomPolicy",
            ["Pipeline:OutputDiff:Rules:0:Pattern"] = "../outside-native-scope/**"
        });
        var snapshotter = new RecordingSnapshotter(artifact, reportUnchanged: true);
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.Services.AddSingleton<PipelineOutputSnapshotter>(_ => throw new InvalidOperationException("Native snapshotter resolved by replacement policy."));
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>();
        using var app = builder.Build();
        Assert.Empty(snapshotter.Events);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".git")));

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));

        Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);
        Assert.Equal(new[] { "capture", "capture", "compare" }, snapshotter.Events);
        Assert.Equal("stable", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GlobalOnlyCaptureDoesNotResolveExternalResourceDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var global = workspace.CreateDirectory("global").FullName;
        var external = workspace.CreateDirectory("external").FullName;
        var artifact = Path.Combine(external, "artifact.txt");
        await File.WriteAllTextAsync(artifact, "old", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: global, step: "publish-and-diff");
        builder.AddResource(new OutputResource("files"));
        var provider = new CountingOutputService(global, external);
        builder.Services.AddSingleton<IPipelineOutputService>(provider);
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, _ => File.WriteAllTextAsync(artifact, "new", TestContext.Current.CancellationToken));
        using var app = builder.Build();
        Assert.Equal("old", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        await ExecuteAsync(app);

        Assert.Equal(2, provider.GlobalCalls);
        Assert.Equal(0, provider.ResourceCalls);
        Assert.Equal("new", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NativeDiffIncludesPublishFinalActionsAndRetainsOutput()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var changed = Path.Combine(workspace.Path, "changed.txt");
        var removed = Path.Combine(workspace.Path, "removed.txt");
        var added = Path.Combine(workspace.Path, "added.txt");
        await File.WriteAllTextAsync(changed, "before", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(removed, "remove me", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            await File.WriteAllTextAsync(changed, "after", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(added, "added", TestContext.Current.CancellationToken);
            File.Delete(removed);
        });
        using var app = builder.Build();
        Assert.Equal("before", await File.ReadAllTextAsync(changed, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(removed));
        Assert.False(File.Exists(added));

        var report = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Equal($"""
            Pipeline output changed:
              Added: {Path.GetRelativePath(workspace.Path, added)}
              Changed: {Path.GetRelativePath(workspace.Path, changed)}
              Removed: {Path.GetRelativePath(workspace.Path, removed)}
            """.ReplaceLineEndings(), report.Message);
        Assert.Equal("after", await File.ReadAllTextAsync(changed, TestContext.Current.CancellationToken));
        Assert.Equal("added", await File.ReadAllTextAsync(added, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(removed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachTransientCaptureIsDisposedOnlyByHost(bool reportUnchanged)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "unchanged.txt");
        await File.WriteAllTextAsync(artifact, "stable", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        var instances = new ConcurrentBag<RecordingSnapshotter>();
        builder.Services.AddTransient<IPipelineOutputSnapshotter<RecordingSnapshot>>(_ =>
        {
            var instance = new RecordingSnapshotter(artifact, reportUnchanged);
            instances.Add(instance);
            return instance;
        });
        builder.Services.AddSingleton<PipelineOutputSnapshotter>(_ => throw new InvalidOperationException("Native engine must not resolve for a replacement."));
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>();
        using var app = builder.Build();
        Assert.Empty(instances);

        if (reportUnchanged)
        {
            var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Contains("Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.ToString());
        }
        else
        {
            await ExecuteAsync(app);
        }

        Assert.Equal(2, instances.Count);
        Assert.Single(instances, snapshotter => snapshotter.Events.SequenceEqual(new[] { "capture" }));
        Assert.Single(instances, snapshotter => snapshotter.Events.SequenceEqual(new[] { "capture", "compare" }));
        Assert.Equal("stable", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.All(instances, snapshotter => Assert.False(snapshotter.Disposed));
        app.Dispose();
        Assert.All(instances, snapshotter => Assert.True(snapshotter.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasedFactoryChecksResolveSeparateCapturesWithoutBaselineCycles(bool selectBoth)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "stable.txt");
        await File.WriteAllTextAsync(artifact, "stable", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: selectBoth ? "both-checks" : "first-check");
        var instances = new ConcurrentBag<RecordingSnapshotter>();
        var comparers = new ConcurrentBag<RecordingComparer>();
        builder.Services.AddTransient<IPipelineOutputSnapshotter<RecordingSnapshot>>(_ =>
        {
            var snapshotter = new RecordingSnapshotter(artifact);
            instances.Add(snapshotter);
            return snapshotter;
        });
        builder.Services.AddTransient<IPipelineOutputComparer<RecordingSnapshot>>(_ =>
        {
            var comparer = new RecordingComparer();
            comparers.Add(comparer);
            return comparer;
        });
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>(() => new PipelineStep
        {
            Name = "first-check",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [WellKnownPipelineSteps.Publish]
        });
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>(() => new PipelineStep
        {
            Name = "second-check",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [WellKnownPipelineSteps.Publish]
        });
        builder.Pipeline.AddStep("both-checks", _ => Task.CompletedTask, dependsOn: new[] { "first-check", "second-check" });
        using var app = builder.Build();
        Assert.Empty(instances);
        Assert.Empty(comparers);

        await ExecuteAsync(app);

        Assert.Equal(selectBoth ? 4 : 2, instances.Count);
        Assert.Equal(selectBoth ? 2 : 1, comparers.Count);
        if (selectBoth)
        {
            var selected = instances.ToArray();
            var selectedComparers = comparers.ToArray();
            Assert.NotSame(selected[0], selected[1]);
            Assert.NotSame(selectedComparers[0], selectedComparers[1]);
        }
        Assert.Equal(selectBoth ? 2 : 1, instances.Count(snapshotter => snapshotter.Events.SequenceEqual(new[] { "capture" })));
        Assert.Equal(selectBoth ? 2 : 1, instances.Count(snapshotter => snapshotter.Events.SequenceEqual(new[] { "capture", "compare" })));
        Assert.Equal("stable", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.All(instances, snapshotter => Assert.False(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.False(comparer.Disposed));
        app.Dispose();
        Assert.All(instances, snapshotter => Assert.True(snapshotter.Disposed));
        Assert.All(comparers, comparer => Assert.True(comparer.Disposed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedBaselineOrTargetPreventsLaterCapturesAndComparison(bool failBaseline)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "result.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        var snapshotter = new RecordingSnapshotter(artifact) { FailBefore = failBaseline };
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>();
        builder.Pipeline.AddStep("writer", async _ =>
        {
            await File.WriteAllTextAsync(artifact, "target entered", TestContext.Current.CancellationToken);
            throw new IOException("target failed");
        }, dependsOn: WellKnownPipelineSteps.PublishPrereq, requiredBy: WellKnownPipelineSteps.Publish);
        using var app = builder.Build();
        Assert.False(File.Exists(artifact));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(app));

        Assert.True(failure is InvalidOperationException or AggregateException, failure.ToString());
        Assert.Contains(failBaseline ? "baseline failed" : "target failed", failure.ToString());
        Assert.Equal(new[] { "capture" }, snapshotter.Events);
        Assert.Equal(!failBaseline, File.Exists(artifact));
        if (!failBaseline)
        {
            Assert.Equal("target entered", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        }
        app.Dispose();
        Assert.False(snapshotter.Disposed); // An externally supplied instance is owned by its caller.
    }

    [Fact]
    public async Task CustomTargetNeedsNoExplicitBaseline()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "custom.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "custom-check");
        var snapshotter = new RecordingSnapshotter(artifact);
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.Pipeline.AddStep("custom-target", _ =>
        {
            Assert.Equal(new[] { "capture" }, snapshotter.Events);
            return File.WriteAllTextAsync(artifact, "custom output", TestContext.Current.CancellationToken);
        });
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>("custom-target", "custom-check");
        using var app = builder.Build();
        Assert.False(File.Exists(artifact));
        Assert.Empty(snapshotter.Events);

        await ExecuteAsync(app);

        Assert.Equal(new[] { "capture", "capture", "compare" }, snapshotter.Events);
        Assert.Equal("custom output", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationFromSnapshotterStopsTheTarget()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "cancelled.txt");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var snapshotter = new RecordingSnapshotter(artifact) { BeforeCancellation = cancellation.Token };
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>();
        builder.Pipeline.AddStep("writer", _ => File.WriteAllTextAsync(artifact, "unexpected", TestContext.Current.CancellationToken),
            dependsOn: WellKnownPipelineSteps.PublishPrereq, requiredBy: WellKnownPipelineSteps.Publish);
        using var app = builder.Build();
        Assert.False(File.Exists(artifact));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(app));

        IEnumerable<Exception> failures = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
        Assert.Contains(failures, exception =>
            exception.InnerException is OperationCanceledException cancelled && cancelled.CancellationToken == cancellation.Token);
        Assert.Equal(new[] { "capture" }, snapshotter.Events);
        Assert.False(File.Exists(artifact));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachDefaultTransientReadsCurrentRuleValues(bool requireGit)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var included = Path.Combine(workspace.Path, "included.txt");
        var excluded = Path.Combine(workspace.Path, "excluded.tmp");
        await File.WriteAllTextAsync(included, "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(excluded, "old", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Services.Configure<PipelineOutputDiffOptions>(options =>
        {
            options.Mode = PipelineOutputDiffMode.FileSystem;
            options.Rules.Add(new() { Action = "Include", Pattern = "**/*" });
            options.Rules.Add(new() { Action = "Exclude", Pattern = "*.tmp" });
        });
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async context =>
        {
            var options = context.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PipelineOutputDiffOptions>>().Value;
            Assert.Equal(new[] { "**/*", "*.tmp" }, options.Rules.Select(rule => rule.Pattern));
            var rule = Assert.Single(options.Rules, rule => rule.Action == "Exclude");
            Assert.Equal("Exclude", rule.Action);
            Assert.Equal("*.tmp", rule.Pattern);
            rule.Action = "Include";
            rule.Pattern = "**/*";
            options.Rules.Clear();
            options.Rules.Add(new() { Action = "Exclude", Pattern = "included.txt" });
            options.Mode = requireGit ? PipelineOutputDiffMode.Git : PipelineOutputDiffMode.FileSystem;
            await File.WriteAllTextAsync(included, "new", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(excluded, "new", TestContext.Current.CancellationToken);
        });
        using var app = builder.Build();
        Assert.Equal("old", await File.ReadAllTextAsync(included, TestContext.Current.CancellationToken));
        Assert.Equal("old", await File.ReadAllTextAsync(excluded, TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));

        if (requireGit)
        {
            Assert.Contains("Git is required", failure.Message);
        }
        else
        {
            Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Removed: " + Path.GetRelativePath(workspace.Path, included), failure.Message);
        }
        Assert.Equal("new", await File.ReadAllTextAsync(excluded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitReplacementUsesIndexBaselineWithoutResolvingNativeSnapshotter()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "tracked.txt");
        var added = Path.Combine(workspace.Path, "untracked.txt");
        var excluded = Path.Combine(workspace.Path, "excluded.tmp");
        await File.WriteAllTextAsync(artifact, "old", TestContext.Current.CancellationToken);
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "init");
        await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "add", "--", "tracked.txt");
        Assert.Empty(await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "diff", "--name-only"));
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        builder.Services.AddSingleton<PipelineOutputSnapshotter>(_ => throw new InvalidOperationException("Native snapshotter resolved by Git replacement."));
        builder.Services.AddTransient<IPipelineOutputSnapshotter<GitSnapshot>>(_ => new GitSnapshotter(workspace.Path));
        AddArtifactWriter(builder.Pipeline);
        builder.WithOutputDiffing<GitSnapshot, GitSnapshotter, GitComparer>();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.Publish, async _ =>
        {
            await File.WriteAllTextAsync(artifact, "new", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(added, "untracked", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(excluded, "excluded", TestContext.Current.CancellationToken);
        });
        using var app = builder.Build();

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));

        Assert.Equal($"""
            Pipeline output changed:
              Changed: {Path.GetRelativePath(workspace.Path, artifact)}
              Added: {Path.GetRelativePath(workspace.Path, added)}
            """.ReplaceLineEndings(), failure.Message);
        Assert.Equal("new", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.Equal("untracked", await File.ReadAllTextAsync(added, TestContext.Current.CancellationToken));
        Assert.Equal("excluded", await File.ReadAllTextAsync(excluded, TestContext.Current.CancellationToken));
        Assert.Equal("old", (await PipelineOutputGitTestHelper.RunAsync(workspace.Path, "show", ":tracked.txt")).Trim());
    }

    [Fact]
    public async Task DifferentSnapshotTypesUseIndependentProvidersAndFreshOutputDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var relocated = workspace.CreateDirectory("relocated").FullName;
        var artifact = Path.Combine(workspace.Path, "stable.txt");
        await File.WriteAllTextAsync(artifact, "stable", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "both-checks");
        var snapshotter = new RecordingSnapshotter(artifact);
        var directories = new DirectorySnapshotter();
        var comparer = new DirectoryComparer(workspace.Path, relocated);
        var provider = new CountingOutputService(workspace.Path, relocated);
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<RecordingSnapshot>>(snapshotter);
        builder.Services.AddSingleton<IPipelineOutputSnapshotter<string>>(directories);
        builder.Services.AddSingleton<IPipelineOutputComparer<string>>(comparer);
        builder.Services.AddSingleton<IPipelineOutputService>(provider);
        builder.WithOutputDiffing<RecordingSnapshot, RecordingSnapshotter, RecordingComparer>(diffStepName: "file-check");
        var factoryCalls = 0;
        builder.WithOutputDiffing<string, DirectorySnapshotter, DirectoryComparer>(() => new PipelineStep
        {
            Name = "directory-check",
            Action = _ =>
            {
                Assert.Equal(new[] { workspace.Path }, directories.Directories);
                Assert.Equal(0, comparer.Comparisons);
                factoryCalls++;
                return Task.CompletedTask;
            },
            DependsOnSteps = [WellKnownPipelineSteps.Publish]
        });
        builder.Pipeline.AddStep("relocate-output", _ =>
        {
            Assert.Equal(new[] { "capture" }, snapshotter.Events);
            Assert.Equal(new[] { workspace.Path }, directories.Directories);
            provider.GlobalDirectory = relocated;
            return Task.CompletedTask;
        }, requiredBy: WellKnownPipelineSteps.Publish);
        builder.Pipeline.AddStep("both-checks", _ => Task.CompletedTask, dependsOn: new[] { "file-check", "directory-check" });
        using var app = builder.Build();
        Assert.Empty(snapshotter.Events);
        Assert.Empty(directories.Directories);
        Assert.Equal(0, comparer.Comparisons);
        Assert.Equal(0, factoryCalls);
        Assert.NotEqual(workspace.Path, relocated);

        await ExecuteAsync(app);

        Assert.Equal(new[] { "capture", "capture", "compare" }, snapshotter.Events);
        Assert.Equal(new[] { workspace.Path, relocated }, directories.Directories);
        Assert.Equal(1, comparer.Comparisons);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(4, provider.GlobalCalls);
        Assert.Equal(0, provider.ResourceCalls);
        Assert.Equal("stable", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
    }

    private static void AddArtifactWriter(IDistributedApplicationPipeline pipeline) => pipeline.AddStep(
        "artifact-writer", _ => Task.CompletedTask,
        dependsOn: WellKnownPipelineSteps.PublishPrereq, requiredBy: WellKnownPipelineSteps.Publish);

    private static Task ExecuteAsync(DistributedApplication app) => app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
        new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken));

    private sealed class OutputResource(string name) : Resource(name);

    private sealed record GitSnapshot(string OutputDirectory, IReadOnlyList<PipelineOutputChange> Changes);

    // Each capture reports working-tree changes against the index, rather than native file fingerprints.
    private sealed class GitSnapshotter(string directory) : IPipelineOutputSnapshotter<GitSnapshot>
    {
        public async Task<GitSnapshot> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(directory, outputDirectory);
            var exclusions = new Microsoft.Extensions.FileSystemGlobbing.Matcher(StringComparison.Ordinal);
            exclusions.AddInclude("**/*");
            exclusions.AddExclude("*.tmp");
            List<PipelineOutputChange> changes = [];
            foreach (var (command, kind) in new[]
            {
                (new[] { "diff", "--name-only", "-z" }, PipelineOutputChangeKind.Changed),
                (new[] { "ls-files", "--others", "--exclude-standard", "-z" }, PipelineOutputChangeKind.Added)
            })
            {
                // Both commands emit paths separated by NUL, including names containing newlines.
                foreach (var path in (await PipelineOutputGitTestHelper.RunAsync(directory, command)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Microsoft.Extensions.FileSystemGlobbing.MatcherExtensions.Match(exclusions, path).HasMatches)
                    {
                        changes.Add(new PipelineOutputChange(kind, path));
                    }
                }
            }

            return new GitSnapshot(outputDirectory, changes.AsReadOnly());
        }
    }

    private sealed class GitComparer : IPipelineOutputComparer<GitSnapshot>
    {
        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(GitSnapshot before, GitSnapshot after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(before.OutputDirectory, after.OutputDirectory);
            // This policy uses the index as its baseline, not the earlier working-tree capture.
            return Task.FromResult(after.Changes);
        }
    }

    private sealed class CountingOutputService(string globalDirectory, string resourceDirectory) : IPipelineOutputService
    {
        private int _globalCalls;
        private int _resourceCalls;
        public int GlobalCalls => _globalCalls;
        public int ResourceCalls => _resourceCalls;
        public string GlobalDirectory { get; set; } = globalDirectory;
        public string GetOutputDirectory()
        {
            Interlocked.Increment(ref _globalCalls);
            return GlobalDirectory;
        }
        public string GetOutputDirectory(IResource resource)
        {
            Interlocked.Increment(ref _resourceCalls);
            return resourceDirectory;
        }
        public string GetTempDirectory() => throw new InvalidOperationException("No temporary lookup during diffing.");
        public string GetTempDirectory(IResource resource) => throw new InvalidOperationException("No temporary lookup during diffing.");
    }

    private sealed record RecordingSnapshot(RecordingSnapshotter Owner, string OutputDirectory, int Capture);

    private sealed class RecordingSnapshotter(string artifact, bool reportUnchanged = false) : IPipelineOutputSnapshotter<RecordingSnapshot>, IDisposable
    {
        public List<string> Events { get; } = [];
        public string Artifact { get; } = artifact;
        public bool ReportUnchanged { get; } = reportUnchanged;
        public bool FailBefore { get; init; }
        public CancellationToken BeforeCancellation { get; init; }
        public bool Disposed { get; private set; }

        public Task<RecordingSnapshot> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            Assert.NotEmpty(outputDirectory);
            Assert.InRange(Events.Count, 0, 1);
            var first = Events.Count == 0;
            Events.Add("capture");
            if (first)
            {
                if (FailBefore)
                {
                    throw new IOException("baseline failed");
                }
                BeforeCancellation.ThrowIfCancellationRequested();
            }
            else
            {
                Assert.True(File.Exists(Artifact));
            }

            return Task.FromResult(new RecordingSnapshot(this, outputDirectory, Events.Count));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingComparer : IPipelineOutputComparer<RecordingSnapshot>, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(RecordingSnapshot before, RecordingSnapshot after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            var sameOwner = ReferenceEquals(before.Owner, after.Owner);
            Assert.Equal(1, before.Capture);
            Assert.Equal(sameOwner ? 2 : 1, after.Capture);
            var snapshotter = before.Owner;
            Assert.False(snapshotter.Disposed);
            Assert.False(after.Owner.Disposed);
            Assert.Equal(sameOwner ? new[] { "capture", "capture" } : new[] { "capture" }, snapshotter.Events);
            Assert.Equal(sameOwner ? new[] { "capture", "capture" } : new[] { "capture" }, after.Owner.Events);
            after.Owner.Events.Add("compare");

            return Task.FromResult<IReadOnlyList<PipelineOutputChange>>(snapshotter.ReportUnchanged
                ? [new(PipelineOutputChangeKind.Changed, Path.GetFileName(snapshotter.Artifact))] : []);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class DirectorySnapshotter : IPipelineOutputSnapshotter<string>
    {
        public List<string> Directories { get; } = [];

        public Task<string> CaptureAsync(string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Directories.Add(outputDirectory);

            return Task.FromResult(outputDirectory);
        }
    }

    private sealed class DirectoryComparer(string beforeDirectory, string afterDirectory) : IPipelineOutputComparer<string>
    {
        public int Comparisons { get; private set; }

        public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(string before, string after, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(beforeDirectory, before);
            Assert.Equal(afterDirectory, after);
            Comparisons++;

            return Task.FromResult<IReadOnlyList<PipelineOutputChange>>([]);
        }
    }
}

#pragma warning restore ASPIREPIPELINES005
