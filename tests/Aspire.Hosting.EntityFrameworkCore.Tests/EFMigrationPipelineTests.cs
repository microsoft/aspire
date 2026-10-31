// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Retains migration publishing compatibility for legacy projects.

#pragma warning disable ASPIREDOTNETTOOL, ASPIREPROJECTS001

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationPipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("linux-x64", "", false)]
    [InlineData("win-x64", ".exe", false)]
    [InlineData("linux-x64", "", true)]
    [InlineData("win-x64", ".exe", true)]
    public async Task BundleContainerBuildReadsGeneratedWorkspaceAndRetainsOutput(string runtime, string suffix, bool customOutputService)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var originalOutput = Path.Combine(workspace.Path, "original");
        var finalOutput = Path.Combine(workspace.Path, "final");
        string contextPath;
        var fileName = "migrations" + suffix;
        var retainedPath = Path.Combine(finalOutput, "efmigrations", fileName);
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: originalOutput, step: "build-migrations"))
        {
            var containerRuntime = new FakeContainerRuntime();
            builder.Services.AddFakeContainerRuntime(containerRuntime);
            builder.Configuration["Parameters:build-secret"] = "secret-value";
            var secret = builder.AddParameter("build-secret", secret: true);
            var project = builder.AddProject<Projects.ServiceA>("api");
            var db = builder.AddResource(new TestDatabaseResource("db"));
            var migrations = project.AddEFMigrations("migrations")
                .WaitFor(db)
                .PublishAsMigrationBundle(targetRuntime: runtime, publishContainer: true, baseImage: "custom-runtime:latest")
                .WithImage("custom-migrations", "test")
                .WithBuildArg("BUNDLE_ARG", "arg-value")
                .WithBuildSecret("BUNDLE_SECRET", secret);
            var dockerfile = Assert.Single(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
            contextPath = dockerfile.ContextPath;
            Assert.False(File.Exists(Path.Combine(contextPath, "migrations" + suffix)));

            // Change the output after container configuration, when its build context is already fixed.
            if (customOutputService)
            {
                builder.Services.AddSingleton<IPipelineOutputService>(new PackagingOutputService(finalOutput));
            }
            else
            {
                builder.Services.Configure<PipelineOptions>(options => options.OutputPath = finalOutput);
            }
            var tool = new TestEfTool
            {
                EmitArtifactAsync = arguments =>
                {
                    var outputPath = arguments[Array.IndexOf(arguments, "--output") + 1];
                    Assert.Equal(Path.Combine(contextPath, fileName), outputPath);
                    Assert.False(File.Exists(retainedPath));
                    return File.WriteAllTextAsync(outputPath, "generated bundle", TestContext.Current.CancellationToken);
                }
            };
            migrations.Resource.ToolResource = tool.Resource;
            object? buildOutput = null;
            containerRuntime.BuildImageAsyncCallback = async (context, dockerfilePath, options, arguments, secrets, _, ct) =>
            {
                Assert.Equal(contextPath, context);
                Assert.Equal("generated bundle", await File.ReadAllTextAsync(Path.Combine(context, fileName), ct));
                Assert.Equal("generated bundle", await File.ReadAllTextAsync(retainedPath, ct));
                buildOutput = new
                {
                    Dockerfile = await File.ReadAllTextAsync(dockerfilePath, ct),
                    options!.ImageName,
                    options.Tag,
                    Arguments = arguments,
                    Secrets = secrets.ToDictionary(secret => secret.Key, secret => secret.Value.Value)
                };
            };
            using var app = builder.Build();

            await ExecutePackagingPipelineAsync(app);

            Assert.Single(tool.Invocations);
            Assert.Single(containerRuntime.BuildImageCalls);
            Assert.False(Directory.Exists(originalOutput));
            Assert.True(migrations.Resource.TryGetContainerImageName(out var imageName));
            Assert.NotNull(buildOutput);
            await Verify(new { Build = buildOutput, Image = imageName });
        }

        Assert.False(Directory.Exists(contextPath));
        Assert.Equal("generated bundle", await File.ReadAllTextAsync(retainedPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleContainerGenerationOrCopyFailurePreventsImageBuild(bool copyFailure)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string contextPath;
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "build-migrations"))
        {
            var containerRuntime = new FakeContainerRuntime();
            builder.Services.AddFakeContainerRuntime(containerRuntime);
            var project = builder.AddProject<Projects.ServiceA>("api");
            var db = builder.AddResource(new TestDatabaseResource("db"));
            var migrations = project.AddEFMigrations("migrations").WaitFor(db)
                .PublishAsMigrationBundle(targetRuntime: "linux-x64", publishContainer: true);
            contextPath = Assert.Single(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>()).ContextPath;
            var retainedPath = Path.Combine(workspace.Path, "efmigrations", "migrations");
            Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
            if (copyFailure)
            {
                // A directory occupying the destination filename fails copying on every platform.
                Directory.CreateDirectory(retainedPath);
            }
            else
            {
                await File.WriteAllTextAsync(retainedPath, "last successful bundle", TestContext.Current.CancellationToken);
            }
            var tool = new TestEfTool
            {
                Result = copyFailure ? CommandResults.Success() : CommandResults.Failure("bundle generation failed"),
                EmitArtifactAsync = arguments =>
                {
                    var outputPath = arguments[Array.IndexOf(arguments, "--output") + 1];
                    Assert.Equal(Path.Combine(contextPath, "migrations"), outputPath);
                    return File.WriteAllTextAsync(outputPath, "incomplete or uncopied bundle", TestContext.Current.CancellationToken);
                }
            };
            migrations.Resource.ToolResource = tool.Resource;
            using var app = builder.Build();
            Assert.Empty(containerRuntime.BuildImageCalls);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecutePackagingPipelineAsync(app));

            Assert.Single(tool.Invocations);
            Assert.Equal("incomplete or uncopied bundle", await File.ReadAllTextAsync(
                Path.Combine(contextPath, "migrations"), TestContext.Current.CancellationToken));
            Assert.Empty(containerRuntime.BuildImageCalls);
            if (copyFailure)
            {
                Assert.True(Directory.Exists(retainedPath));
            }
            else
            {
                Assert.Contains("bundle generation failed", exception.ToString());
                Assert.Equal("last successful bundle", await File.ReadAllTextAsync(retainedPath, TestContext.Current.CancellationToken));
            }
        }

        Assert.False(Directory.Exists(contextPath));
    }

    [Theory]
    [InlineData(false, "linux-x64", ".sql")]
    [InlineData(true, "linux-x64", "")]
    [InlineData(true, "win-x64", ".exe")]
    public async Task NonContainerGenerationWritesDirectlyToOutput(bool bundle, string runtime, string suffix)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path,
            step: bundle ? "migrations-generate-migration-bundle" : "migrations-generate-migration-script");
        var project = builder.AddProject<Projects.ServiceA>("api");
        var migrations = project.AddEFMigrations("migrations");
        if (bundle)
        {
            migrations.PublishAsMigrationBundle(targetRuntime: runtime);
        }
        else
        {
            migrations.PublishAsMigrationScript();
        }
        Assert.Empty(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        var expectedPath = Path.Combine(workspace.Path, "efmigrations", "migrations" + suffix);
        var tool = new TestEfTool
        {
            EmitArtifactAsync = arguments =>
            {
                Assert.Equal(expectedPath, arguments[Array.IndexOf(arguments, "--output") + 1]);
                return File.WriteAllTextAsync(expectedPath, "direct artifact", TestContext.Current.CancellationToken);
            }
        };
        migrations.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();
        Assert.False(File.Exists(expectedPath));

        await ExecutePackagingPipelineAsync(app);

        Assert.Single(tool.Invocations);
        Assert.Equal("direct artifact", await File.ReadAllTextAsync(expectedPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BundleContainersUseIsolatedWorkspaces()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "bundle-images");
        var project = builder.AddProject<Projects.ServiceA>("api");
        var db = builder.AddResource(new TestDatabaseResource("db"));
        var first = project.AddEFMigrations("first", typeof(TestDbContext).FullName!).WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "linux-x64", publishContainer: true);
        var second = project.AddEFMigrations("second", typeof(AnotherDbContext).FullName!).WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "linux-x64", publishContainer: true);
        var firstContext = Assert.Single(first.Resource.Annotations.OfType<DockerfileBuildAnnotation>()).ContextPath;
        var secondContext = Assert.Single(second.Resource.Annotations.OfType<DockerfileBuildAnnotation>()).ContextPath;
        Assert.NotEqual(firstContext, secondContext);
        Assert.True(Directory.Exists(firstContext));
        Assert.True(Directory.Exists(secondContext));
        var tool = new TestEfTool
        {
            EmitArtifactAsync = arguments =>
            {
                var outputPath = arguments[Array.IndexOf(arguments, "--output") + 1];
                return File.WriteAllTextAsync(outputPath, Path.GetFileName(outputPath), TestContext.Current.CancellationToken);
            }
        };
        first.Resource.ToolResource = tool.Resource;
        second.Resource.ToolResource = tool.Resource;
        var containerRuntime = new FakeContainerRuntime
        {
            BuildImageAsyncCallback = async (context, _, _, _, _, _, ct) =>
            {
                var name = context == firstContext ? "first" : "second";
                Assert.Equal(name == "first" ? firstContext : secondContext, context);
                Assert.Equal(name, await File.ReadAllTextAsync(Path.Combine(context, name), ct));
                Assert.False(File.Exists(Path.Combine(context, name == "first" ? "second" : "first")));
            }
        };
        builder.Services.AddFakeContainerRuntime(containerRuntime);
        builder.Pipeline.AddStep("bundle-images", _ => Task.CompletedTask, dependsOn: new[] { "build-first", "build-second" });
        using var app = builder.Build();

        await ExecutePackagingPipelineAsync(app);

        Assert.Equal(new[] { firstContext, secondContext }.Order(), containerRuntime.BuildImageCalls.Select(call => call.contextPath).Order());
        Assert.Equal(2, tool.Invocations.Count);
        await Verify(new
        {
            FirstBundle = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "efmigrations", "first"), TestContext.Current.CancellationToken),
            SecondBundle = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "efmigrations", "second"), TestContext.Current.CancellationToken)
        });
    }

    private static Task ExecutePackagingPipelineAsync(DistributedApplication app)
    {
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services, NullLogger.Instance, TestContext.Current.CancellationToken);
        return app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context);
    }

    [Fact]
    public async Task BundleManifestBuildContextSurvivesAppHostDisposal()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-manifest"))
        {
            var project = builder.AddProject<Projects.ServiceA>("api");
            var db = builder.AddResource(new TestDatabaseResource("db"));
            var migrations = project.AddEFMigrations("migrations").WaitFor(db)
                .PublishAsMigrationBundle(targetRuntime: "linux-x64", publishContainer: true);
            var tool = new TestEfTool
            {
                EmitArtifactAsync = arguments => File.WriteAllTextAsync(
                    arguments[Array.IndexOf(arguments, "--output") + 1], "manifest bundle", TestContext.Current.CancellationToken)
            };
            migrations.Resource.ToolResource = tool.Resource;
            migrations.WithPipelineConfiguration(context => context.Steps.Single(s => s.Name == "publish-manifest")
                .DependsOn("migrations-generate-migration-bundle"));
            using var app = builder.Build();
            Assert.False(File.Exists(Path.Combine(workspace.Path, "efmigrations", "migrations")));

            await ExecutePackagingPipelineAsync(app);
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aspire-manifest.json"), TestContext.Current.CancellationToken))!;
        var manifest = document["resources"]!["migrations"]!;
        var contextPath = Path.GetFullPath(manifest["build"]!["context"]!.GetValue<string>(), workspace.Path);
        var bundlePath = Path.Combine(contextPath, "migrations");

        Assert.Equal("manifest bundle", await File.ReadAllTextAsync(bundlePath, TestContext.Current.CancellationToken));
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aspire-manifest.json"), TestContext.Current.CancellationToken)
        }).ScrubProjectPaths(Path.Combine(workspace.Path, "aspire-manifest.json"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CustomBuildWarningPreservesPublishingResult(bool bundle, bool fail)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        using var services = builder.Services.BuildServiceProvider();
        using var workspace = services.GetRequiredService<IFileSystemService>().TempDirectory.CreateTempSubdirectory();
        builder.Services.Configure<PipelineOptions>(options => options.OutputPath = workspace.Path);
        var callbackCalls = 0;
        var project = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath,
            options => options.ExcludeLaunchProfile = true).WithBuildEnvironment(_ => { callbackCalls++; });
        var migrations = project.AddEFMigrations("migrations");
        if (bundle)
        {
            migrations.PublishAsMigrationBundle();
        }
        else
        {
            migrations.PublishAsMigrationScript();
        }
        var tool = new TestEfTool
        {
            Result = fail ? CommandResults.Failure("EF output is missing") : CommandResults.Success()
        };
        migrations.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();
        var sink = new TestSink();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(), builder.ExecutionContext, app.Services,
            new TestLogger("EF", sink, level => level >= LogLevel.Information), TestContext.Current.CancellationToken);
        var steps = new List<PipelineStep>();
        foreach (var annotation in migrations.Resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = context,
                Resource = migrations.Resource
            }));
        }
        var step = Assert.Single(steps);
        var stepContext = new PipelineStepContext { PipelineContext = context, ReportingStep = null! };

        if (fail)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => step.Action(stepContext));
            Assert.Contains("EF output is missing", exception.Message);
        }
        else
        {
            await step.Action(stepContext);
        }

        var invocation = Assert.Single(tool.Invocations);
        Assert.DoesNotContain("--no-build", invocation);
        Assert.Single(sink.Writes, write => write.LogLevel == LogLevel.Warning);
        Assert.Equal(0, callbackCalls);
    }

    [Fact]
    public async Task BundleOnlyProducesGenerateStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var generateStep = Assert.Single(steps);
        Assert.Equal("mymigrations-generate-migration-bundle", generateStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Publish, generateStep.RequiredBySteps);
        Assert.DoesNotContain(WellKnownPipelineSteps.Build, generateStep.RequiredBySteps);
        Assert.Empty(generateStep.DependsOnSteps);
    }

    [Fact]
    public async Task ScriptOnlyProducesScriptStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var scriptStep = Assert.Single(steps);
        Assert.Equal("mymigrations-generate-migration-script", scriptStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Publish, scriptStep.RequiredBySteps);
    }

    [Fact]
    public async Task ScriptAndBundleProducesAllStepsWithDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Equal(2, steps.Count);

        var scriptStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-script");
        var bundleStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");

        // Bundle generation depends on script to avoid parallel tool usage
        Assert.Contains(scriptStep.Name, bundleStep.DependsOnSteps);
    }

    [Fact]
    public async Task NoPublishOptionsProducesNoSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Empty(steps);
    }

    [Fact]
    public async Task MultipleBundlesOnSameProjectAreSerializedByChainingSteps()
    {
        // Two migration resources targeting the same startup project must not generate their
        // bundles in parallel: each `dotnet-ef migrations bundle` drives a `dotnet build` of
        // that shared project, and concurrent builds corrupt the project's obj/bin output.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var m1 = project.AddEFMigrations("aaa-migrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();
        var m2 = project.AddEFMigrations("bbb-migrations", typeof(AnotherDbContext).FullName!)
            .PublishAsMigrationBundle();

        var m1Steps = await CreateStepsAsync(builder, m1.Resource);
        var m2Steps = await CreateStepsAsync(builder, m2.Resource);

        var m1Bundle = Assert.Single(m1Steps, s => s.Name == "aaa-migrations-generate-migration-bundle");
        var m2Bundle = Assert.Single(m2Steps, s => s.Name == "bbb-migrations-generate-migration-bundle");

        Assert.Empty(m1Bundle.DependsOnSteps);
        Assert.Contains("aaa-migrations-generate-migration-bundle", m2Bundle.DependsOnSteps);
    }

    [Fact]
    public async Task MultipleMigrationsOnSameProjectChainScriptAndBundleAcrossResources()
    {
        // The successor migration's first step (its script step when present, otherwise its bundle
        // step) must depend on the predecessor's last step (its bundle step when present, otherwise
        // its script step). Within a single migration the bundle already depends on the script, so
        // we only need to attach the cross-migration edge to the first step.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var m1 = project.AddEFMigrations("aaa-migrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle();
        var m2 = project.AddEFMigrations("bbb-migrations", typeof(AnotherDbContext).FullName!)
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle();

        var m1Steps = await CreateStepsAsync(builder, m1.Resource);
        var m2Steps = await CreateStepsAsync(builder, m2.Resource);

        var m1Script = Assert.Single(m1Steps, s => s.Name == "aaa-migrations-generate-migration-script");
        var m1Bundle = Assert.Single(m1Steps, s => s.Name == "aaa-migrations-generate-migration-bundle");
        var m2Script = Assert.Single(m2Steps, s => s.Name == "bbb-migrations-generate-migration-script");
        var m2Bundle = Assert.Single(m2Steps, s => s.Name == "bbb-migrations-generate-migration-bundle");

        Assert.Empty(m1Script.DependsOnSteps);
        Assert.Contains(m1Script.Name, m1Bundle.DependsOnSteps);
        Assert.Contains(m1Bundle.Name, m2Script.DependsOnSteps);
        Assert.Contains(m2Script.Name, m2Bundle.DependsOnSteps);
        Assert.DoesNotContain(m1Bundle.Name, m2Bundle.DependsOnSteps);
    }

    [Fact]
    public async Task MigrationsOnDifferentProjectsAreAlsoChained()
    {
        // Migrations on different projects must still be serialized: every `dotnet-ef` invocation
        // touches per-user state (`dotnet tool exec` cache, NuGet restore caches, MSBuild
        // node-reuse) that is not safe under concurrent `dotnet-ef` runs even when the projects
        // don't overlap. The chain spans the whole model, not just one project.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var projectA = builder.AddProject<Projects.ServiceA>("projecta");
        var projectB = builder.AddProject<Projects.ServiceA>("projectb");
        var m1 = projectA.AddEFMigrations("aaa-migrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();
        var m2 = projectB.AddEFMigrations("bbb-migrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var m1Steps = await CreateStepsAsync(builder, m1.Resource);
        var m2Steps = await CreateStepsAsync(builder, m2.Resource);

        var m1Bundle = Assert.Single(m1Steps);
        var m2Bundle = Assert.Single(m2Steps);

        Assert.Empty(m1Bundle.DependsOnSteps);
        Assert.Contains("aaa-migrations-generate-migration-bundle", m2Bundle.DependsOnSteps);
    }

    [Fact]
    public async Task MixedScriptOnlyAndBundleOnlyMigrationsAreChained()
    {
        // A predecessor with only a script step and a successor with only a bundle step should
        // still produce a single serialized chain: bundle -> script via DependsOnSteps.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var m1 = project.AddEFMigrations("aaa-migrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript();
        var m2 = project.AddEFMigrations("bbb-migrations", typeof(AnotherDbContext).FullName!)
            .PublishAsMigrationBundle();

        var m1Steps = await CreateStepsAsync(builder, m1.Resource);
        var m2Steps = await CreateStepsAsync(builder, m2.Resource);

        var m1Script = Assert.Single(m1Steps);
        var m2Bundle = Assert.Single(m2Steps);

        Assert.Equal("aaa-migrations-generate-migration-script", m1Script.Name);
        Assert.Equal("bbb-migrations-generate-migration-bundle", m2Bundle.Name);
        Assert.Contains(m1Script.Name, m2Bundle.DependsOnSteps);
    }

    [Fact]
    public async Task MigrationsWithoutPublishOptionsAreSkippedFromChain()
    {
        // A sibling migration that opted out of publish-time generation produces no pipeline
        // steps, so it must not appear in the chain — otherwise the successor would depend on
        // a non-existent step name and pipeline execution would fail.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var m1 = project.AddEFMigrations("aaa-migrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();
        // bbb-migrations does NOT call PublishAsMigrationBundle/Script — it has no pipeline steps.
        _ = project.AddEFMigrations("bbb-migrations", typeof(AnotherDbContext).FullName!);
        var m3 = project.AddEFMigrations("ccc-migrations", typeof(ThirdDbContext).FullName!)
            .PublishAsMigrationBundle();

        var m1Steps = await CreateStepsAsync(builder, m1.Resource);
        var m3Steps = await CreateStepsAsync(builder, m3.Resource);

        var m1Bundle = Assert.Single(m1Steps);
        var m3Bundle = Assert.Single(m3Steps);

        Assert.Contains("aaa-migrations-generate-migration-bundle", m3Bundle.DependsOnSteps);
        Assert.DoesNotContain("bbb-migrations-generate-migration-bundle", m3Bundle.DependsOnSteps);
        Assert.DoesNotContain("bbb-migrations-generate-migration-script", m3Bundle.DependsOnSteps);
        Assert.Empty(m1Bundle.DependsOnSteps);
    }

    [Fact]
    public async Task PublishBundleContainerProducesNoStepsInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .RunDatabaseUpdateOnStart()
            .PublishAsMigrationBundle(publishContainer: true);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Empty(steps);
        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.True(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void WaitForConnectionStringResourceAddsWaitAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db);

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Single(waitAnnotations);

        // The waited-on resource should be the IResourceWithConnectionString
        var waitedResource = waitAnnotations[0].Resource;
        Assert.IsAssignableFrom<IResourceWithConnectionString>(waitedResource);
        Assert.Equal("mydb", waitedResource.Name);
    }

    [Fact]
    public void WaitForMultipleResourcesAddsMultipleAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db1 = builder.AddResource(new TestDatabaseResource("db1"));
        var db2 = builder.AddResource(new TestDatabaseResource("db2"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db1)
            .WaitFor(db2);

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Equal(2, waitAnnotations.Count);
    }

    [Fact]
    public void MigrationWithoutWaitAnnotationsHasNoConnectionStringDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Empty(waitAnnotations);
    }

    [Fact]
    public void AddEFMigrations_HiddenToolResourceHasDedicatedStartCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var startCommand = migrations.Resource.ToolResource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .SingleOrDefault(a => a.Name == EFCoreOperationExecutor.ToolStartCommandName);

        Assert.NotNull(startCommand);
    }

    [Fact]
    public void PublishAsMigrationBundleSetsResourceProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "linux-x64", selfContained: true, publishContainer: true);

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.Equal("linux-x64", migrations.Resource.BundleTargetRuntime);
        Assert.True(migrations.Resource.BundleSelfContained);
        Assert.True(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void PublishAsMigrationBundleDefaultsPublishContainerToFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.False(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void PublishAsMigrationScriptSetsResourceProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript(idempotent: true, noTransactions: true);

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.ScriptIdempotent);
        Assert.True(migrations.Resource.ScriptNoTransactions);
        Assert.False(migrations.Resource.PublishAsMigrationBundle);
    }

    [Fact]
    public void PublishBundleContainerDefaultsTargetRuntimeToLinuxX64()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        Assert.Equal("linux-x64", migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void PublishBundleContainerPreservesUserSpecifiedTargetRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "linux-arm64", publishContainer: true);

        Assert.Equal("linux-arm64", migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void PublishBundleContainerDoesNotForceSelfContained()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        Assert.False(migrations.Resource.BundleSelfContained);
    }

    [Fact]
    public void WithoutPublishContainerNoTargetRuntimeDefaultIsApplied()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.Null(migrations.Resource.BundleTargetRuntime);
    }

    [Fact]
    public void EFMigrationResourceIsAComputeResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // EFMigrationResource now inherits from ContainerResource so a compute environment can deploy it
        // as a container when PublishBundleContainer is true. Without that option no container image
        // is associated with the resource, so the compute environment ignores it (IsContainer() is false).
        Assert.IsAssignableFrom<ContainerResource>(migrations.Resource);
        Assert.IsAssignableFrom<IComputeResource>(migrations.Resource);
        Assert.False(migrations.Resource.IsContainer());
    }

    [Fact]
    public void PublishBundleContainerAddsDockerfileBuildAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfileAnnotation = Assert.Single(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(dockerfileAnnotation.DockerfileFactory);
        Assert.True(Directory.Exists(dockerfileAnnotation.ContextPath));
        Assert.True(migrations.Resource.IsContainer());
    }

    [Fact]
    public void PublishBundleContainerDoesNotAddDockerfileInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        // The container-image wiring is only meaningful during `aspire publish`. In run mode
        // the user interacts with the migration resource via its dashboard commands, so no
        // container image should be materialized locally even when publishContainer: true.
        Assert.Empty(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.False(migrations.Resource.IsContainer());

        // Flags on the resource itself are still set so subsequent publish runs behave correctly.
        Assert.True(migrations.Resource.PublishBundleContainer);
    }

    [Fact]
    public void PublishBundleContainerDoesNotAddDockerfileWhenOptionIsFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.Empty(migrations.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.False(migrations.Resource.IsContainer());
    }

    [Fact]
    public void GeneratedDockerfileReferencesWaitedOnConnectionStringEnvVar()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:10.0", dockerfile);
        // Bundle file name follows the target runtime (linux-x64 default => no .exe suffix).
        Assert.Contains("COPY --chmod=0755 mymigrations /app/efbundle", dockerfile);
        Assert.DoesNotContain("RUN chmod", dockerfile);
        Assert.Contains("ENTRYPOINT /app/efbundle -v --connection \"$ConnectionStrings__mydb\"", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfileUsesPortableConnectionStringEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("my-db"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Equal(
            """
            # Auto-generated by Aspire for EF Core migration bundle.
            FROM mcr.microsoft.com/dotnet/runtime:10.0
            WORKDIR /app
            COPY --chmod=0755 mymigrations /app/efbundle
            ENTRYPOINT /app/efbundle -v --connection "$ConnectionStrings__my_db"

            """,
            dockerfile);
    }

    [Fact]
    public void GeneratedDockerfileUsesSelfContainedBaseImageWhenRequested()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(selfContained: true, publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime-deps:10.0", dockerfile);
        Assert.DoesNotContain("chiseled", dockerfile);
    }

    [Theory]
    [InlineData("net11.0", "mcr.microsoft.com/dotnet/runtime:11.0")]
    [InlineData("net10.0", "mcr.microsoft.com/dotnet/runtime:10.0")]
    [InlineData("net8.0", "mcr.microsoft.com/dotnet/runtime:10.0")] // clamped to minimum
    [InlineData(null, "mcr.microsoft.com/dotnet/runtime:10.0")] // no framework resolved yet
    public void ResolveBaseImageUsesResolvedFrameworkWithMinimumFloor(string? resolvedFramework, string expectedImage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        migrations.Resource.ResolvedFramework = resolvedFramework;

        var baseImage = EFMigrationResourceBuilderExtensions.ResolveBaseImage(migrations.Resource);
        Assert.Equal(expectedImage, baseImage);
    }

    [Fact]
    public void ResolveBaseImageReturnsUserOverrideVerbatim()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true, baseImage: "myregistry.io/custom-runtime:preview");

        // Even if a framework is resolved, the user override wins.
        migrations.Resource.ResolvedFramework = "net11.0";

        var baseImage = EFMigrationResourceBuilderExtensions.ResolveBaseImage(migrations.Resource);
        Assert.Equal("myregistry.io/custom-runtime:preview", baseImage);
    }

    [Fact]
    public void ResolveBaseImageAppendsWindowsTagForWinRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "win-x64", publishContainer: true);

        var baseImage = EFMigrationResourceBuilderExtensions.ResolveBaseImage(migrations.Resource);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:10.0-nanoserver-ltsc2022", baseImage);
    }

    [Fact]
    public void GeneratedDockerfileUsesWindowsPathsAndEnvSyntaxForWinRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(targetRuntime: "win-x64", publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:10.0-nanoserver-ltsc2022", dockerfile);
        Assert.Contains("WORKDIR C:\\app", dockerfile);
        Assert.Contains("COPY mymigrations.exe C:\\app\\efbundle.exe", dockerfile);
        Assert.DoesNotContain("--chmod", dockerfile);
        // Windows uses %VAR% for env-var expansion via cmd.exe.
        Assert.Contains("ENTRYPOINT C:\\app\\efbundle.exe -v --connection \"%ConnectionStrings__mydb%\"", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfileFailsWhenNoWaitedOnConnectionStringResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(publishContainer: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource));
        Assert.Contains("'.WaitFor(<database>)'", ex.Message);
    }

    [Fact]
    public void GeneratedDockerfileFailsWhenMultipleUnrelatedWaitedOnConnectionStringResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db1 = builder.AddResource(new TestDatabaseResource("db1"));
        var db2 = builder.AddResource(new TestDatabaseResource("db2"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db1)
            .WaitFor(db2)
            .PublishAsMigrationBundle(publishContainer: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource));
        Assert.Contains("multiple resources", ex.Message);
        Assert.Contains("'db1'", ex.Message);
        Assert.Contains("'db2'", ex.Message);
    }

    [Fact]
    public void GeneratedDockerfileUsesReferencedConnectionStringResource()
    {
        // .WithReference(db) is the explicit way for the user to declare which connection
        // string the bundle should target. It must be honored even when no .WaitFor is set.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithReference(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("ConnectionStrings__mydb", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfilePrefersReferencedResourceOverWaitedOnResource()
    {
        // When the user explicitly references one database via .WithReference(db) and waits on
        // another via .WaitFor(other), the explicit reference is the user's stated target and
        // must win — the waited-on resource is just an ordering signal, not a target selection.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var referenced = builder.AddResource(new TestDatabaseResource("referenced"));
        var waited = builder.AddResource(new TestDatabaseResource("waited"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithReference(referenced)
            .WaitFor(waited)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("ConnectionStrings__referenced", dockerfile);
        Assert.DoesNotContain("ConnectionStrings__waited", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfileFailsWhenMultipleUnrelatedReferencedConnectionStringResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db1 = builder.AddResource(new TestDatabaseResource("db1"));
        var db2 = builder.AddResource(new TestDatabaseResource("db2"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithReference(db1)
            .WithReference(db2)
            .PublishAsMigrationBundle(publishContainer: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource));
        Assert.Contains("multiple resources", ex.Message);
        Assert.Contains("'db1'", ex.Message);
        Assert.Contains("'db2'", ex.Message);
    }

    [Fact]
    public void GeneratedDockerfilePrefersLeafWhenReferencedChildAndParent()
    {
        // Mirrors the WaitFor leaf-vs-ancestor test: when the user .WithReference's both a
        // child database and its parent server, the leaf (child) is the one whose connection
        // string targets the actual database, so it wins.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var server = builder.AddResource(new TestDatabaseResource("sql"));
        var database = builder.AddResource(new TestChildDatabaseResource("sqldata", server.Resource));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WithReference(server)
            .WithReference(database)
            .PublishAsMigrationBundle(publishContainer: true);

        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);

        Assert.Contains("ConnectionStrings__sqldata", dockerfile);
    }

    [Fact]
    public void GeneratedDockerfilePrefersLeafWhenWaitForChildAlsoWaitsOnParent()
    {
        // WaitForStart on an IResourceWithParent dependency automatically adds a wait annotation
        // for the parent as well (ResourceBuilderExtensions.WaitForStartCore). That puts BOTH the
        // server and the database in the candidate set here — but the leaf (database) is the one
        // whose connection string should be injected into the bundle container.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var server = builder.AddResource(new TestDatabaseResource("sql"));
        var database = builder.AddResource(new TestChildDatabaseResource("sqldata", server.Resource));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitForStart(database)
            .PublishAsMigrationBundle(publishContainer: true);

        // Sanity check: the automatic parent-wait really does add the server as a second candidate.
        var waitedResources = migrations.Resource.Annotations.OfType<WaitAnnotation>().Select(w => w.Resource).ToList();
        Assert.Contains(server.Resource, waitedResources);
        Assert.Contains((IResource)database.Resource, waitedResources);

        // The leaf (child) resource's connection string is what the bundle should use.
        var dockerfile = EFMigrationResourceBuilderExtensions.GenerateDockerfile(migrations.Resource);
        Assert.Contains("ConnectionStrings__sqldata", dockerfile);
    }

    [Fact]
    public void BundleFileNameForWindowsRuntimeHasExeSuffix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(targetRuntime: "win-x64");

        Assert.Equal("mymigrations.exe", migrations.Resource.GetBundleFileName());
    }

    [Fact]
    public void BundleFileNameForLinuxRuntimeHasNoExeSuffix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(targetRuntime: "linux-arm64");

        Assert.Equal("mymigrations", migrations.Resource.GetBundleFileName());
    }

    [Fact]
    public async Task PublishBundleContainerMakesBuildStepDependOnGenerateStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db)
            .PublishAsMigrationBundle(publishContainer: true);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var generateStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");

        // The generate step names the specific image-build step in RequiredBySteps. Relying only
        // on RequiredBy(Build) wouldn't be enough: both the generate step and the `build-<name>`
        // step are siblings of the Build aggregation step, so without a direct edge they run in
        // parallel and the image build reads a half-written bundle from the Docker build context.
        Assert.Contains($"build-{migrations.Resource.Name}", generateStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.Publish, generateStep.RequiredBySteps);
    }

    [Fact]
    public void GetToolEnvironmentVariablesOmitsConnectionStringPlaceholdersInPublishMode()
    {
        var db = new TestDatabaseResource("postgresdb");
        var config = new TestExecutionConfigurationResult
        {
            EnvironmentVariablesWithUnprocessed =
            [
                new("ConnectionStrings__postgresdb", (new ConnectionStringReference(db, optional: false), "{postgresdb.connectionString}")),
                new("OTHER", ("not-a-connection-string", "value")),
            ]
        };

        var result = EFResourceBuilderExtensions.GetToolEnvironmentVariables(config, isPublishMode: true).ToList();

        // The connection string placeholder is omitted so the EF tool doesn't receive an invalid value.
        Assert.DoesNotContain(result, kvp => kvp.Key == "ConnectionStrings__postgresdb");
        Assert.Contains(result, kvp => kvp.Key == "OTHER" && kvp.Value == "value");
    }

    [Fact]
    public void GetToolEnvironmentVariablesKeepsConnectionStringsInRunMode()
    {
        var db = new TestDatabaseResource("postgresdb");
        var config = new TestExecutionConfigurationResult
        {
            EnvironmentVariablesWithUnprocessed =
            [
                new("ConnectionStrings__postgresdb", (new ConnectionStringReference(db, optional: false), "Host=localhost;Database=postgresdb")),
            ]
        };

        var result = EFResourceBuilderExtensions.GetToolEnvironmentVariables(config, isPublishMode: false).ToList();

        // In run mode the connection string resolves to a real value and must be forwarded to the tool.
        Assert.Contains(result, kvp => kvp.Key == "ConnectionStrings__postgresdb" && kvp.Value == "Host=localhost;Database=postgresdb");
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(
        IDistributedApplicationTestingBuilder builder,
        EFMigrationResource migrationResource)
    {
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var pipelineContext = new PipelineContext(
            serviceProvider.GetRequiredService<DistributedApplicationModel>(),
            builder.ExecutionContext,
            serviceProvider,
            NullLogger.Instance,
            CancellationToken.None);

        var results = new List<PipelineStep>();
        foreach (var annotation in migrationResource.Annotations.OfType<PipelineStepAnnotation>())
        {
            results.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = migrationResource
            }));
        }

        return results;
    }

    private sealed class PackagingOutputService(string path) : IPipelineOutputService
    {
        public string GetOutputDirectory() => path;
        public string GetOutputDirectory(IResource resource) => Path.Combine(path, resource.Name);
        public string GetTempDirectory() => Path.Combine(path, "temp");
        public string GetTempDirectory(IResource resource) => Path.Combine(GetTempDirectory(), resource.Name);
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }
    private sealed class AnotherDbContext { }
    private sealed class ThirdDbContext { }

    /// <summary>
    /// A minimal test resource that implements IResourceWithConnectionString and IResourceWithWaitSupport.
    /// </summary>
    private sealed class TestDatabaseResource(string name) : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Host=localhost;Database={Name}");
    }

    /// <summary>
    /// A minimal test child resource that links to a parent via IResourceWithParent.
    /// Mirrors the SqlServerDatabase/SqlServerServer pattern where WaitFor(child) transitively
    /// adds a WaitAnnotation for the parent.
    /// </summary>
    private sealed class TestChildDatabaseResource(string name, TestDatabaseResource parent)
        : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport, IResourceWithParent<TestDatabaseResource>
    {
        public TestDatabaseResource Parent { get; } = parent;

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{Parent};Database={Name}");
    }

    /// <summary>
    /// A minimal <see cref="IExecutionConfigurationResult"/> used to exercise environment-variable
    /// selection without starting a real EF tool process.
    /// </summary>
    private sealed class TestExecutionConfigurationResult : IExecutionConfigurationResult
    {
        public IEnumerable<object> References { get; init; } = [];
        public IEnumerable<(object Unprocessed, string Processed, bool IsSensitive)> ArgumentsWithUnprocessed { get; init; } = [];
        public IEnumerable<(string Value, bool IsSensitive)> Arguments => ArgumentsWithUnprocessed.Select(a => (a.Processed, a.IsSensitive));
        public IEnumerable<KeyValuePair<string, (object Unprocessed, string Processed)>> EnvironmentVariablesWithUnprocessed { get; init; } = [];
        public IEnumerable<KeyValuePair<string, string>> EnvironmentVariables => EnvironmentVariablesWithUnprocessed.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value.Processed));
        public IEnumerable<IExecutionConfigurationData> AdditionalConfigurationData { get; init; } = [];
        public Exception? Exception { get; init; }
    }
}
