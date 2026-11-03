// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFOutputDiffingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ActualGenerationGraphWaitsForBaselineAndPreservesSerialization(bool script, bool bundle, bool separateProjects)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "inspect");
        var project = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath);
        var secondProject = separateProjects ? builder.AddDotnetProject("other", new Projects.ServiceB().ProjectPath) : project;
        var first = project.AddEFMigrations("first", "FirstContext");
        var second = secondProject.AddEFMigrations("second", "SecondContext");
        foreach (var migration in new[] { first, second })
        {
            if (script)
            {
                migration.PublishAsMigrationScript();
            }
            if (bundle)
            {
                migration.PublishAsMigrationBundle(targetRuntime: "linux-x64", publishContainer: true);
            }
        }
        builder.WithOutputDiffing();
        IReadOnlyList<PipelineStep> graph = [];
        builder.Pipeline.AddStep("inspect", _ => Task.CompletedTask);
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            graph = context.Steps.ToArray();
            return Task.CompletedTask;
        });
        using var app = builder.Build();
        await ExecuteAsync(app);
        var generations = graph.Where(step => step.Name.Contains("-generate-migration-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(script && bundle ? 4 : 2, generations.Length);
        foreach (var generation in generations)
        {
            // Each generation's independently selected closure must include the baseline, not the comparison target.
            Assert.True(Reaches(graph, generation.Name, "publish-and-diff-baseline"), generation.Name);
            Assert.False(Reaches(graph, generation.Name, "publish-and-diff"));
        }
        var firstLast = bundle ? "first-generate-migration-bundle" : "first-generate-migration-script";
        var secondHead = script ? "second-generate-migration-script" : "second-generate-migration-bundle";
        Assert.True(Reaches(graph, secondHead, firstLast));
        if (bundle)
        {
            Assert.True(Reaches(graph, "build-first", "first-generate-migration-bundle"));
            Assert.True(Reaches(graph, "build-second", "second-generate-migration-bundle"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualEfWriterRunsAfterBaselineAndRetainsChangedArtifact(bool bundle)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var artifact = Path.Combine(workspace.Path, "efmigrations", bundle ? "migrations" : "migrations.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "old migration", TestContext.Current.CancellationToken);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-and-diff");
        var project = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath);
        var migration = project.AddEFMigrations("migrations");
        if (bundle)
        {
            migration.PublishAsMigrationBundle(targetRuntime: "linux-x64");
        }
        else
        {
            migration.PublishAsMigrationScript();
        }
        var captured = 0;
        builder.Services.AddSingleton<IPipelineOutputFileHasher>(new TestPipelineOutputFileHasher(async (file, ct) =>
        {
            Assert.Equal(artifact, file.FullName);
            using var reader = File.OpenText(file.FullName);
            var content = await reader.ReadToEndAsync(ct);
            if (Interlocked.CompareExchange(ref captured, 1, 0) == 0)
            {
                Assert.Equal("old migration", content);
            }
            return System.Text.Encoding.UTF8.GetBytes(content);
        }));
        var tool = new TestEfTool
        {
            EmitArtifactAsync = arguments =>
            {
                Assert.Equal(1, Volatile.Read(ref captured));
                Assert.Equal(artifact, arguments[Array.IndexOf(arguments, "--output") + 1]);
                return File.WriteAllTextAsync(artifact, "new migration", TestContext.Current.CancellationToken);
            }
        };
        migration.Resource.ToolResource = tool.Resource;
        builder.WithOutputDiffing();
        using var app = builder.Build();
        Assert.Equal("old migration", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));

        Assert.Equal("Pipeline output changed:" + Environment.NewLine + "  Changed: " + Path.GetRelativePath(workspace.Path, artifact), failure.Message);
        Assert.Equal("new migration", await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken));
        Assert.Single(tool.Invocations);
    }

    private static bool Reaches(IReadOnlyList<PipelineStep> steps, string from, string target)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string name) => name == target || (visited.Add(name) &&
            steps.Single(step => step.Name == name).DependsOnSteps.Any(Visit));
        return Visit(from);
    }

    private static Task ExecuteAsync(DistributedApplication app) => app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
        new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, TestContext.Current.CancellationToken));
}

#pragma warning restore ASPIREPIPELINES005
