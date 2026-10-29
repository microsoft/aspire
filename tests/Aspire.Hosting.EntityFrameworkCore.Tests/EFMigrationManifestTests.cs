// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationManifestTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ManifestRetainsSharedContextAfterDisposal(bool customProvider, bool namedManifest)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var paths = GetManifestOutputPaths(workspace.Path, customProvider, namedManifest);
        var retained = Path.Combine(paths.OutputDirectory, "efmigrations");
        DockerfileBuildAnnotation[] annotations;
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: paths.OutputPath, step: "publish-manifest"))
        {
            var runtime = new FakeContainerRuntime();
            builder.Services.AddFakeContainerRuntime(runtime);
            var first = AddMigration(builder);
            var second = builder.AddProject<Projects.ServiceA>("second-api").AddEFMigrations("second")
                .WaitFor(builder.AddResource(new TestDatabaseResource("second-db")))
                .PublishAsMigrationBundle(targetRuntime: "win-x64", publishContainer: true);
            annotations = new[] { first, second }
                .Select(m => Assert.Single(m.Resource.Annotations.OfType<DockerfileBuildAnnotation>())).ToArray();
            var tool = CreateBundleTool();
            foreach (var migration in new[] { first, second })
            {
                migration.Resource.ToolResource = tool.Resource;
                RequireGenerationForManifest(migration);
            }
            // Resolve the provider at execution time, not when EF configures its temporary workspace.
            if (customProvider)
            {
                builder.Services.AddSingleton<IPipelineOutputService>(new OutputService(paths.OutputDirectory));
            }
            Assert.False(File.Exists(paths.ManifestPath));
            Assert.False(Directory.Exists(retained));
            using var app = builder.Build();

            await Execute(app, builder);

            Assert.Equal(2, tool.Invocations.Count);
            Assert.Empty(runtime.BuildImageCalls);
            foreach (var (migration, annotation) in new[] { first, second }.Zip(annotations))
            {
                Assert.Same(annotation, Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>()));
                Assert.True(Directory.Exists(annotation.ContextPath));
            }
        }

        Assert.All(annotations, annotation => Assert.False(Directory.Exists(annotation.ContextPath)));
        var manifestDirectory = Path.GetDirectoryName(paths.ManifestPath)!;
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(paths.ManifestPath, TestContext.Current.CancellationToken),
            Bundles = await ReadOutputFiles(retained, "migrations", "second.exe"),
            Dockerfiles = await ReadOutputFiles(manifestDirectory, "migrations.Dockerfile", "second.Dockerfile"),
            OutputDirectories = Directory.GetDirectories(paths.OutputDirectory).Select(Path.GetFileName).Order()
        }).ScrubProjectPaths(paths.ManifestPath);
        if (customProvider)
        {
            Assert.Empty(Directory.GetDirectories(manifestDirectory));
        }
    }

    [Fact]
    public async Task ManifestPreservesCallbackIdentityAndRelocatesOnlyContextFileSecrets()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string source;
        var retained = Path.Combine(workspace.Path, "efmigrations");
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-manifest"))
        {
            var migration = AddMigration(builder);
            var original = Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
            source = original.ContextPath;
            // Keep the literal path portable so only the temporary directory needs scrubbing.
            var sourceSecret = Path.Combine(source, "nested", "secret.txt").Replace('\\', '/');
            var externalSecret = Path.Combine(workspace.Path, "external-secret.txt");
            await WriteFile(sourceSecret, "secret");
            await WriteFile(externalSecret, "external");
            var factoryCalls = 0;
            var environmentCalls = 0;
            var forwardedToolCalls = 0;
            var originalTool = builder.Resources.Single(r => r.Name == "ef-tool-migrations");
            DockerfileBuildAnnotation? factoryAnnotation = null;
            var annotation = new DockerfileBuildAnnotation(source, original.DockerfilePath, original.Stage)
            {
                DockerfileFactory = async context =>
                {
                    Assert.Same(migration.Resource, context.Resource);
                    factoryAnnotation = Assert.Single(context.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
                    Assert.Equal(source, factoryAnnotation.ContextPath);
                    factoryCalls++;
                    await WriteFile(Path.Combine(source, "nested", "factory.txt"), "factory input");
                    return await original.DockerfileFactory!(context) + "\nCOPY nested /extra\n";
                }
            };
            annotation.BuildArguments["CUSTOM"] = "value";
            annotation.BuildSecrets["FILE"] = new FileInfo(sourceSecret);
            annotation.BuildSecrets["EXTERNAL"] = new FileInfo(externalSecret);
            annotation.BuildSecrets["LITERAL"] = sourceSecret;
            migration.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
            migration.WithEnvironment(context =>
            {
                // EF also forwards this callback to its original dotnet-ef tool.
                if (ReferenceEquals(context.Resource, originalTool))
                {
                    forwardedToolCalls++;
                    return;
                }
                Assert.Same(migration.Resource, context.Resource);
                Assert.Same(annotation, Assert.Single(context.Resource.Annotations.OfType<DockerfileBuildAnnotation>()));
                Assert.Equal(source, annotation.ContextPath);
                environmentCalls++;
                File.WriteAllText(Path.Combine(source, "nested", "late.txt"), "environment input");
                context.EnvironmentVariables["CUSTOM_ENV"] = "preserved";
            });
            var tool = CreateBundleTool();
            migration.Resource.ToolResource = tool.Resource;
            RequireGenerationForManifest(migration);
            using var app = builder.Build();

            await Execute(app, builder);

            Assert.Single(tool.Invocations);
            Assert.Equal(1, factoryCalls);
            Assert.Same(annotation, factoryAnnotation);
            Assert.Equal(1, environmentCalls);
            Assert.Equal(1, forwardedToolCalls);
            Assert.Same(annotation, Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>()));
            Assert.Equal(source, annotation.ContextPath);
            Assert.True(Directory.Exists(source));
        }

        Assert.False(Directory.Exists(source));
        var manifestPath = Path.Combine(workspace.Path, "aspire-manifest.json");
        var resource = (await ReadManifest(manifestPath))["resources"]!["migrations"]!;
        var build = resource["build"]!;
        var retainedSecret = Path.GetFullPath(build["secrets"]!["FILE"]!["source"]!.GetValue<string>(), workspace.Path);
        var exportedExternalSecret = Path.GetFullPath(build["secrets"]!["EXTERNAL"]!["source"]!.GetValue<string>(), workspace.Path);
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken),
            Secret = await File.ReadAllTextAsync(retainedSecret, TestContext.Current.CancellationToken),
            ExternalSecret = await File.ReadAllTextAsync(exportedExternalSecret, TestContext.Current.CancellationToken),
            CallbackFiles = await ReadOutputFiles(retained, "nested/factory.txt", "nested/late.txt")
        }).ScrubProjectPaths(manifestPath)
          .ScrubLinesWithReplace(line => line.Replace(source.Replace('\\', '/'), "<build-context>"));
    }

    [Theory]
    [InlineData(false, false, false)] // Generated sibling ignore.
    [InlineData(false, true, false)] // Authored context root overrides generated sibling.
    [InlineData(false, false, true)] // Retained root overrides generated sibling.
    [InlineData(true, false, false)] // Preserve the static Dockerfile's authored sibling.
    [InlineData(true, true, false)] // Preserve authored sibling even with a context-root ignore.
    public async Task ManifestPreservesDockerIgnorePrecedence(bool staticDockerfile, bool contextIgnore, bool retainedIgnore)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        DockerfileBuildAnnotation original;
        var retained = Path.Combine(workspace.Path, "efmigrations");
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-manifest"))
        {
            var migration = AddMigration(builder);
            original = Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
            var sourceDockerfile = Path.Combine(original.ContextPath, "nested", "Containerfile");
            await WriteFile(sourceDockerfile, "FROM example/image\nCOPY migrations /bundle\n");
            if (staticDockerfile)
            {
                await WriteFile(sourceDockerfile + ".dockerignore", "authored-sibling\n");
            }
            migration.WithAnnotation(new DockerfileBuildAnnotation(
                original.ContextPath, staticDockerfile ? sourceDockerfile : original.DockerfilePath, original.Stage)
            {
                BuildContextIgnoreContent = "**/ignored\n",
                DockerfileFactory = staticDockerfile ? null : original.DockerfileFactory
            }, ResourceAnnotationMutationBehavior.Replace);
            if (contextIgnore)
            {
                await WriteFile(Path.Combine(original.ContextPath, ".dockerignore"), "authored-ignore\n");
            }
            if (retainedIgnore)
            {
                await WriteFile(Path.Combine(retained, ".dockerignore"), "authored-ignore\n");
            }
            Assert.False(File.Exists(Path.Combine(workspace.Path, "migrations.Dockerfile.dockerignore")));
            using var app = builder.Build();

            await Execute(app, builder);
        }

        Assert.False(Directory.Exists(original.ContextPath));
        var manifestPath = Path.Combine(workspace.Path, "aspire-manifest.json");
        var build = (await ReadManifest(manifestPath))["resources"]!["migrations"]!["build"]!;
        var dockerfile = Path.GetFullPath(build["dockerfile"]!.GetValue<string>(), workspace.Path);
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken),
            Dockerfile = await File.ReadAllTextAsync(dockerfile, TestContext.Current.CancellationToken),
            DockerfileIgnore = await ReadOutputFiles(Path.GetDirectoryName(dockerfile)!, Path.GetFileName(dockerfile) + ".dockerignore"),
            ContextIgnore = await ReadOutputFiles(retained, ".dockerignore")
        }).ScrubProjectPaths(manifestPath);
    }

    [Fact]
    public async Task ManifestRetentionCopiesFilesAndUnixLinksWithoutFollowingCycles()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string source;
        var retained = Path.Combine(workspace.Path, "efmigrations");
        var external = Path.Combine(workspace.Path, "external.txt");
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-manifest"))
        {
            var migration = AddMigration(builder);
            source = Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>()).ContextPath;
            var secret = Path.Combine(source, "nested", "secret.txt");
            await WriteFile(secret, "secret");
            Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
            await WriteFile(external, "external");
            await WriteFile(Path.Combine(retained, "nested", "secret.txt"), "previous export");
            await WriteFile(Path.Combine(retained, "unrelated.txt"), "unrelated output");
            await AssertFileContent(Path.Combine(retained, "nested", "secret.txt"), "previous export");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.CreateSymbolicLink(Path.Combine(source, "relative-secret"), Path.Combine("nested", "secret.txt"));
                File.CreateSymbolicLink(Path.Combine(source, "absolute-secret"), secret);
                Directory.CreateSymbolicLink(Path.Combine(source, "nested", "cycle"), "..");
                File.CreateSymbolicLink(Path.Combine(retained, "relative-secret"), external);
                await AssertFileContent(Path.Combine(retained, "relative-secret"), "external");
            }
            using var app = builder.Build();

            await Execute(app, builder);
        }

        Assert.False(Directory.Exists(source));
        await AssertFileContent(Path.Combine(retained, "nested", "secret.txt"), "secret");
        await AssertFileContent(Path.Combine(retained, "unrelated.txt"), "unrelated output");
        await AssertFileContent(external, "external");
        Assert.True(Directory.Exists(Path.Combine(retained, "nested", "empty")));
        Assert.Equal(new[] { "nested" }, Directory.GetDirectories(retained).Select(Path.GetFileName).Order().ToArray());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(retained, "nested", "secret.txt")));
            await AssertFileContent(Path.Combine(retained, "relative-secret"), "secret");
            await AssertFileContent(Path.Combine(retained, "absolute-secret"), "secret");
            await AssertFileContent(Path.Combine(retained, "nested", "cycle", "nested", "secret.txt"), "secret");
        }
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aspire-manifest.json"), TestContext.Current.CancellationToken)
        }).ScrubProjectPaths(Path.Combine(workspace.Path, "aspire-manifest.json"));
    }

    [Fact]
    public async Task ManifestRetentionDoesNotRedirectConcurrentImageBuild()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string source;
        using (var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "combined"))
        {
            var migration = AddMigration(builder);
            var annotation = Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
            source = annotation.ContextPath;
            var callback = Assert.Single(migration.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()).Callback!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            migration.WithManifestPublishingCallback(async context =>
            {
                // Hold manifest export open while the sibling image build reads the live annotation.
                entered.TrySetResult();
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(30), context.CancellationToken);
                await callback(context);
            });
            var tool = CreateBundleTool();
            migration.Resource.ToolResource = tool.Resource;
            builder.Pipeline.AddStep("manifest-entered", async context =>
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), context.CancellationToken));
            RequireGenerationForManifest(migration);
            migration.WithPipelineConfiguration(context => context.Steps.Single(s => s.Name == "build-migrations").DependsOn("manifest-entered"));
            builder.Pipeline.AddStep("combined", _ => Task.CompletedTask, dependsOn: new[] { "publish-manifest", "build-migrations" });
            var runtime = new FakeContainerRuntime
            {
                BuildImageAsyncCallback = async (context, _, _, _, _, _, ct) =>
                {
                    Assert.Equal(source, context);
                    Assert.Same(annotation, Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>()));
                    Assert.Equal(source, annotation.ContextPath);
                    Assert.Equal("bundle", await File.ReadAllTextAsync(Path.Combine(context, "migrations"), ct));
                    observed.TrySetResult();
                }
            };
            builder.Services.AddFakeContainerRuntime(runtime);
            using var app = builder.Build();
            Assert.Empty(runtime.BuildImageCalls);

            await Execute(app, builder);

            Assert.Single(tool.Invocations);
            Assert.Single(runtime.BuildImageCalls);
        }

        Assert.False(Directory.Exists(source));
        Assert.Equal("bundle", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "efmigrations", "migrations"), TestContext.Current.CancellationToken));
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aspire-manifest.json"), TestContext.Current.CancellationToken)
        }).ScrubProjectPaths(Path.Combine(workspace.Path, "aspire-manifest.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestRespectsCustomSerializerOrReplacedContext(bool customSerializer)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-manifest");
        var migration = AddMigration(builder);
        var annotation = Assert.Single(migration.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        if (customSerializer)
        {
            migration.WithManifestPublishingCallback(context => context.Writer.WriteString("type", "custom.v0"));
        }
        else
        {
            migration.WithAnnotation(new DockerfileBuildAnnotation(workspace.Path, annotation.DockerfilePath, annotation.Stage)
            {
                DockerfileFactory = _ => Task.FromResult("FROM example/image")
            }, ResourceAnnotationMutationBehavior.Replace);
        }
        var tool = CreateBundleTool();
        migration.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();

        await Execute(app, builder);

        var manifestPath = Path.Combine(workspace.Path, "aspire-manifest.json");
        Assert.Empty(tool.Invocations);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "efmigrations")));
        await Verify(new
        {
            Manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken)
        }).ScrubProjectPaths(manifestPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestOnlyDoesNotGenerateBundleAndPropagatesRetentionFailure(bool blockCopy)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputPath: workspace.Path, step: "publish-manifest");
        var migration = AddMigration(builder);
        var tool = CreateBundleTool();
        migration.Resource.ToolResource = tool.Resource;
        var destination = Path.Combine(workspace.Path, "efmigrations");
        if (blockCopy)
        {
            await File.WriteAllTextAsync(destination, "blocked by file", TestContext.Current.CancellationToken);
            Assert.True(File.Exists(destination));
        }
        Assert.False(Directory.Exists(destination));
        Assert.Empty(tool.Invocations);
        using var app = builder.Build();

        var exception = await Record.ExceptionAsync(() => Execute(app, builder));

        Assert.Empty(tool.Invocations);
        if (blockCopy)
        {
            Assert.NotNull(exception);
            Assert.Contains("efmigrations", exception.ToString());
            Assert.Equal("blocked by file", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Null(exception);
            Assert.False(File.Exists(Path.Combine(destination, "migrations")));
            await Verify(new
            {
                Manifest = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "aspire-manifest.json"), TestContext.Current.CancellationToken)
            }).ScrubProjectPaths(Path.Combine(workspace.Path, "aspire-manifest.json"));
        }
    }

    private static (string OutputPath, string OutputDirectory, string ManifestPath) GetManifestOutputPaths(
        string workspace, bool customProvider, bool namedManifest)
    {
        var manifestDirectory = Path.Combine(workspace, "export");
        var manifestPath = Path.Combine(manifestDirectory, namedManifest ? "custom.json" : "aspire-manifest.json");
        return (
            namedManifest ? manifestPath : manifestDirectory,
            customProvider ? Path.Combine(workspace, "provider") : manifestDirectory,
            manifestPath);
    }

    private static async Task<Dictionary<string, string?>> ReadOutputFiles(string directory, params string[] paths)
    {
        var files = new Dictionary<string, string?>();
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(directory, path);
            files[path] = File.Exists(fullPath)
                ? await File.ReadAllTextAsync(fullPath, TestContext.Current.CancellationToken)
                : null;
        }
        return files;
    }

    private static IResourceBuilder<EFMigrationResource> AddMigration(IDistributedApplicationBuilder builder) =>
        builder.AddProject<Projects.ServiceA>("api").AddEFMigrations("migrations")
            .WaitFor(builder.AddResource(new TestDatabaseResource("db")))
            .PublishAsMigrationBundle(targetRuntime: "linux-x64", publishContainer: true);

    private static TestEfTool CreateBundleTool() => new()
    {
        EmitArtifactAsync = arguments => File.WriteAllTextAsync(
            arguments[Array.IndexOf(arguments, "--output") + 1], "bundle", TestContext.Current.CancellationToken)
    };

    private static void RequireGenerationForManifest(IResourceBuilder<EFMigrationResource> migration) =>
        migration.WithPipelineConfiguration(context => context.Steps.Single(s => s.Name == "publish-manifest")
            .DependsOn($"{migration.Resource.Name}-generate-migration-bundle"));

    private static async Task WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
    }

    private static async Task AssertFileContent(string path, string expected) =>
        Assert.Equal(expected, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

    private static async Task<JsonNode> ReadManifest(string path) =>
        JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;

    private static async Task Execute(DistributedApplication app, IDistributedApplicationBuilder builder)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(), builder.ExecutionContext,
            app.Services, NullLogger.Instance, timeout.Token));
    }

    private sealed class OutputService(string path) : IPipelineOutputService
    {
        public string GetOutputDirectory() => path;
        public string GetOutputDirectory(IResource resource) => Path.Combine(path, resource.Name);
        public string GetTempDirectory() => Path.Combine(path, "temp");
        public string GetTempDirectory(IResource resource) => Path.Combine(GetTempDirectory(), resource.Name);
    }

    private sealed class TestDatabaseResource(string name) : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport
    {
        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"Host=localhost;Database={Name}");
    }
}
