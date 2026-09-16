// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001

using System.Collections.Concurrent;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

public class DotnetProgramArchiveTests(ITestOutputHelper output)
{
    private const string ImageName = "registry.example.com:5000/team/program";
    private const string ImageTag = "release";
    private const string ImageReference = $"{ImageName}:{ImageTag}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayeredArchivePreservesExistingLocalImages(bool usePipeline)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runner = CreateProcessRunner(images, "sdk");
        var runtime = CreateRuntime(images);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.AddFakeContainerRuntime(runtime);
        var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar");
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, archivePath);
        using var app = builder.Build();

        await BuildAsync(app, resource.Resource, usePipeline, TestContext.Current.CancellationToken).DefaultTimeout();

        AssertImagesUnchanged(existingImages, images);
        Assert.Equal([ImageReference], TestContainerImageArchive.ReadDockerImageReferences(archivePath));
        Assert.Equal("sdk+assets", TestContainerImageArchive.ReadLayerContents(archivePath));
        Assert.Empty(runtime.TagImageCalls);
        var sdkReference = GetSdkImageReference(runner.ProcessSpecs[0]);
        var layeredOptions = Assert.Single(runtime.BuildImageCalls).options!;
        Assert.Equal(
            new[] { sdkReference, $"{layeredOptions.ImageName}:{layeredOptions.Tag}" }.Order(StringComparer.Ordinal),
            runtime.RemoveImageCalls.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ConcurrentArchivesOwnDistinctImages()
    {
        using var firstWorkspace = TemporaryWorkspace.Create(output);
        using var secondWorkspace = TemporaryWorkspace.Create(output);
        using var firstBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var secondBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runtime = CreateRuntime(images);
        var firstRunner = CreateProcessRunner(images, "first-sdk");
        var secondRunner = CreateProcessRunner(images, "second-sdk");
        firstBuilder.Services.AddSingleton<IProcessRunner>(firstRunner);
        secondBuilder.Services.AddSingleton<IProcessRunner>(secondRunner);
        firstBuilder.Services.AddFakeContainerRuntime(runtime);
        secondBuilder.Services.AddFakeContainerRuntime(runtime);
        var firstArchive = Path.Combine(firstWorkspace.WorkspaceRoot.FullName, "program.tar");
        var secondArchive = Path.Combine(secondWorkspace.WorkspaceRoot.FullName, "program.tar");
        var firstResource = AddArchiveResource(firstBuilder, firstWorkspace.WorkspaceRoot.FullName, firstArchive);
        var secondResource = AddArchiveResource(secondBuilder, secondWorkspace.WorkspaceRoot.FullName, secondArchive);
        using var firstApp = firstBuilder.Build();
        using var secondApp = secondBuilder.Build();
        var bothLayersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLayers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layerCount = 0;
        var buildLayer = runtime.BuildImageAsyncCallback!;
        runtime.BuildImageAsyncCallback = async (context, dockerfile, options, arguments, secrets, stage, cancellationToken) =>
        {
            if (Interlocked.Increment(ref layerCount) == 2)
            {
                bothLayersStarted.TrySetResult();
            }

            await releaseLayers.Task.WaitAsync(cancellationToken);
            await buildLayer(context, dockerfile, options, arguments, secrets, stage, cancellationToken);
        };

        var firstBuild = BuildAsync(firstApp, firstResource.Resource, false, TestContext.Current.CancellationToken);
        var secondBuild = BuildAsync(secondApp, secondResource.Resource, false, TestContext.Current.CancellationToken);
        try
        {
            await bothLayersStarted.Task.DefaultTimeout();
        }
        finally
        {
            releaseLayers.TrySetResult();
        }

        await Task.WhenAll(firstBuild, secondBuild).DefaultTimeout();

        AssertImagesUnchanged(existingImages, images);
        Assert.Equal("first-sdk+assets", TestContainerImageArchive.ReadLayerContents(firstArchive));
        Assert.Equal("second-sdk+assets", TestContainerImageArchive.ReadLayerContents(secondArchive));
        Assert.Equal([ImageReference], TestContainerImageArchive.ReadDockerImageReferences(firstArchive));
        Assert.Equal([ImageReference], TestContainerImageArchive.ReadDockerImageReferences(secondArchive));
        Assert.NotEqual(GetSdkImageReference(firstRunner.ProcessSpecs[0]), GetSdkImageReference(secondRunner.ProcessSpecs[0]));
        Assert.Equal(4, runtime.RemoveImageCalls.Count);
        Assert.Equal(4, runtime.RemoveImageCalls.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task FailedSdkPublishCleansOnlyItsPrivateImage()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runner = new TestProcessRunner
        {
            RunCallback = spec => images[GetSdkImageReference(spec)] = "partial-sdk"
        };
        runner.EnqueueResult(exitCode: 42, error: ["publish failed after writing the image"]);
        var runtime = CreateRuntime(images);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.AddFakeContainerRuntime(runtime);
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar"));
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(
            () => BuildAsync(app, resource.Resource, false, TestContext.Current.CancellationToken));

        Assert.Equal(42, exception.ExitCode);
        AssertImagesUnchanged(existingImages, images);
        Assert.Equal(GetSdkImageReference(Assert.Single(runner.ProcessSpecs)), Assert.Single(runtime.RemoveImageCalls));
        Assert.Empty(runtime.BuildImageCalls);
    }

    [Fact]
    public async Task CanceledWorkingDirectoryQueryCleansTheSdkImageWithAnIndependentToken()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runner = new TestProcessRunner
        {
            RunCallback = spec =>
            {
                if (spec.ArgumentList![0] == "publish")
                {
                    images[GetSdkImageReference(spec)] = "sdk";
                }
                else
                {
                    cancellation.Cancel();
                }
            }
        };
        runner.EnqueueResult();
        runner.EnqueuePending(new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var runtime = CreateRuntime(images);
        var removeImage = runtime.RemoveImageAsyncCallback!;
        runtime.RemoveImageAsyncCallback = (image, cleanupToken) =>
        {
            Assert.True(cleanupToken.CanBeCanceled);
            Assert.False(cleanupToken.IsCancellationRequested);
            return removeImage(image, cleanupToken);
        };
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.AddFakeContainerRuntime(runtime);
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar"));
        using var app = builder.Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuildAsync(app, resource.Resource, false, cancellation.Token)).DefaultTimeout();

        AssertImagesUnchanged(existingImages, images);
        Assert.Equal(GetSdkImageReference(runner.ProcessSpecs[0]), Assert.Single(runtime.RemoveImageCalls));
        Assert.Empty(runtime.BuildImageCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayerFailurePreservesExistingImagesAndArchive(bool cancel)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runner = CreateProcessRunner(images, "sdk");
        var runtime = CreateRuntime(images);
        var buildLayer = runtime.BuildImageAsyncCallback!;
        var failure = new InvalidOperationException("layer archive save failed");
        runtime.BuildImageAsyncCallback = async (context, dockerfile, options, arguments, secrets, stage, token) =>
        {
            await buildLayer(context, dockerfile, options, arguments, secrets, stage, token);
            if (cancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            throw failure;
        };
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.AddFakeContainerRuntime(runtime);
        var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar.gz");
        await File.WriteAllTextAsync(archivePath, "previous archive");
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, archivePath);
        using var app = builder.Build();

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BuildAsync(app, resource.Resource, false, cancellation.Token)).DefaultTimeout();
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => BuildAsync(app, resource.Resource, false, cancellation.Token));
            Assert.Same(failure, exception);
        }

        AssertImagesUnchanged(existingImages, images);
        Assert.Equal("previous archive", await File.ReadAllTextAsync(archivePath));
        Assert.Equal(2, runtime.RemoveImageCalls.Count);
        Assert.False(Directory.Exists(Assert.Single(runtime.BuildImageCalls).options!.OutputPath));
    }

    [Fact]
    public async Task InvalidArchivePreservesExistingImagesAndOutput()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runtime = CreateRuntime(images);
        var buildLayer = runtime.BuildImageAsyncCallback!;
        runtime.BuildImageAsyncCallback = async (context, dockerfile, options, arguments, secrets, stage, cancellationToken) =>
        {
            await buildLayer(context, dockerfile, options, arguments, secrets, stage, cancellationToken);
            var archivePath = ResourceExtensions.GetContainerImageArchivePath(options!.OutputPath!, options.ImageName!, options.Tag);
            await File.WriteAllTextAsync(archivePath, "not an image archive", cancellationToken);
        };
        builder.Services.AddSingleton<IProcessRunner>(CreateProcessRunner(images, "sdk"));
        builder.Services.AddFakeContainerRuntime(runtime);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar");
        await File.WriteAllTextAsync(outputPath, "previous archive");
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, outputPath);
        using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => BuildAsync(app, resource.Resource, false, TestContext.Current.CancellationToken));

        AssertImagesUnchanged(existingImages, images);
        Assert.Equal("previous archive", await File.ReadAllTextAsync(outputPath));
        Assert.Equal(2, runtime.RemoveImageCalls.Count);
    }

    [Fact]
    public async Task FinalizationFailurePreservesExistingImagesAndFiles()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var images = CreateExistingImages();
        var existingImages = images.ToArray();
        var runtime = CreateRuntime(images);
        builder.Services.AddSingleton<IProcessRunner>(CreateProcessRunner(images, "sdk"));
        builder.Services.AddFakeContainerRuntime(runtime);
        var blockedDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "output");
        await File.WriteAllTextAsync(blockedDirectory, "existing file");
        var resource = AddArchiveResource(
            builder,
            workspace.WorkspaceRoot.FullName,
            Path.Combine(blockedDirectory, "program.tar.gz"));
        using var app = builder.Build();

        await Assert.ThrowsAsync<IOException>(
            () => BuildAsync(app, resource.Resource, false, TestContext.Current.CancellationToken));

        AssertImagesUnchanged(existingImages, images);
        Assert.Equal("existing file", await File.ReadAllTextAsync(blockedDirectory));
        Assert.Equal(2, runtime.RemoveImageCalls.Count);
        Assert.False(Directory.Exists(Assert.Single(runtime.BuildImageCalls).options!.OutputPath));
    }

    [Fact]
    public async Task CleanupFailuresDoNotReplaceTheLayerFailure()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddLogging(logging => logging.AddFakeLogging());
        var images = CreateExistingImages();
        var runtime = CreateRuntime(images);
        var failure = new InvalidOperationException("layer build failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        runtime.BuildImageAsyncCallback = (_, _, _, _, _, _, _) => throw failure;
        runtime.RemoveImageAsyncCallback = (_, _) => throw cleanupFailure;
        builder.Services.AddSingleton<IProcessRunner>(CreateProcessRunner(images, "sdk"));
        builder.Services.AddFakeContainerRuntime(runtime);
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar"));
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(app, resource.Resource, false, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        var warnings = app.Services.GetFakeLogCollector().GetSnapshot()
            .Where(static record => record.Level == LogLevel.Warning &&
                record.Message.StartsWith("Failed to remove temporary container image", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, record => Assert.Same(cleanupFailure, record.Exception));
    }

    [Theory]
    [InlineData("program.tar", false)]
    [InlineData("program.tar.gz", false)]
    [InlineData("program.tgz", false)]
    [InlineData("program.custom", false)]
    [InlineData("archives", true)]
    [InlineData("archives.v1", true)]
    public async Task LayeredArchivePreservesOutputPathConventions(string path, bool directory)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var images = CreateExistingImages();
        var runtime = CreateRuntime(images);
        builder.Services.AddSingleton<IProcessRunner>(CreateProcessRunner(images, "sdk"));
        builder.Services.AddFakeContainerRuntime(runtime);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, path);
        if (directory)
        {
            outputPath += Path.DirectorySeparatorChar;
        }

        var expectedArchivePath = directory
            ? ResourceExtensions.GetContainerImageArchivePath(outputPath, ImageName, ImageTag)
            : outputPath;
        var resource = AddArchiveResource(builder, workspace.WorkspaceRoot.FullName, outputPath);
        using var app = builder.Build();

        await BuildAsync(app, resource.Resource, false, TestContext.Current.CancellationToken);

        IValueProvider imageReference = new ContainerImageReference(resource.Resource);
        var resolvedPath = await imageReference.GetValueAsync(
            new ValueProviderContext
            {
                ExecutionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>()
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedArchivePath, resolvedPath);
        Assert.Equal([ImageReference], TestContainerImageArchive.ReadDockerImageReferences(expectedArchivePath));
        Assert.Equal("sdk+assets", TestContainerImageArchive.ReadLayerContents(expectedArchivePath));
        Assert.False(Directory.Exists(Assert.Single(runtime.BuildImageCalls).options!.OutputPath));
    }

    private static IResourceBuilder<ProjectResource> AddArchiveResource(
        IDistributedApplicationBuilder builder,
        string workspacePath,
        string outputPath)
    {
        var assets = builder.AddContainer("assets", "assets-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/assets" });
        return builder.AddProject("program", Path.Combine(workspacePath, "program.csproj"), static options => options.ExcludeLaunchProfile = true)
            .WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = assets.Resource,
                DestinationPath = "/app/assets"
            })
            .WithContainerBuildOptions(context =>
            {
                context.LocalImageName = ImageName;
                context.LocalImageTag = ImageTag;
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.OutputPath = outputPath;
            });
    }

    private static ConcurrentDictionary<string, string> CreateExistingImages() => new(StringComparer.Ordinal)
    {
        [ImageReference] = "existing-image",
        ["unrelated:keep"] = "existing-image"
    };

    private static TestProcessRunner CreateProcessRunner(ConcurrentDictionary<string, string> images, string contents)
    {
        var runner = new TestProcessRunner
        {
            RunCallback = spec =>
            {
                if (spec.ArgumentList![0] == "publish")
                {
                    images[GetSdkImageReference(spec)] = contents;
                }
            }
        };
        runner.EnqueueResult();
        runner.EnqueueResult(output: ["/app"]);

        return runner;
    }

    private static string GetSdkImageReference(ProcessSpec spec)
    {
        // SDK invocations carry separate properties, for example:
        // --property:ContainerRepository=registry.example.com:5000/team/program --property:ContainerImageTag=release
        var repository = Assert.Single(spec.ArgumentList!, static argument => argument.StartsWith("--property:ContainerRepository=", StringComparison.Ordinal));
        var tag = Assert.Single(spec.ArgumentList!, static argument => argument.StartsWith("--property:ContainerImageTag=", StringComparison.Ordinal));

        return $"{repository["--property:ContainerRepository=".Length..]}:{tag["--property:ContainerImageTag=".Length..]}";
    }

    private static FakeContainerRuntime CreateRuntime(ConcurrentDictionary<string, string> images)
    {
        return new FakeContainerRuntime(name: "Docker")
        {
            TagImageAsyncCallback = (source, destination, _) =>
            {
                images[NormalizeImageReference(destination)] = images[NormalizeImageReference(source)];
                return Task.CompletedTask;
            },
            RemoveImageAsyncCallback = (image, cancellationToken) =>
            {
                Assert.True(images.TryRemove(NormalizeImageReference(image), out _), $"Image '{image}' was not created.");
                return Task.CompletedTask;
            },
            BuildImageAsyncCallback = (_, dockerfile, options, _, _, _, _) =>
            {
                var buildOptions = Assert.IsType<ContainerImageBuildOptions>(options);
                Assert.Equal(ContainerImageDestination.Archive, buildOptions.Destination);
                // The generated final stage is a plain "FROM repository:tag"; preceding FROM lines name asset stages.
                var from = File.ReadLines(dockerfile).Last(static line => line.StartsWith("FROM ", StringComparison.Ordinal));
                var contents = images[NormalizeImageReference(from["FROM ".Length..])] + "+assets";
                var imageReference = $"{buildOptions.ImageName}:{buildOptions.Tag}";
                images[imageReference] = contents;
                var archivePath = ResourceExtensions.GetContainerImageArchivePath(
                    buildOptions.OutputPath!,
                    buildOptions.ImageName!,
                    buildOptions.Tag);
                TestContainerImageArchive.WriteDockerArchive(archivePath, imageReference, contents);
                return Task.CompletedTask;
            }
        };
    }

    private static string NormalizeImageReference(string reference)
    {
        // "program" implies ":latest", but the colon in "registry:5000/team/program" is not a tag separator.
        return reference.LastIndexOf(':') > reference.LastIndexOf('/') ? reference : $"{reference}:latest";
    }

    private static void AssertImagesUnchanged(
        KeyValuePair<string, string>[] expected,
        ConcurrentDictionary<string, string> actual)
    {
        Assert.Equal(
            expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            actual.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    private static Task BuildAsync(DistributedApplication app, IResource resource, bool usePipeline, CancellationToken cancellationToken)
    {
        if (!usePipeline)
        {
            return app.Services.GetRequiredService<IResourceContainerImageManager>().BuildImageAsync(resource, cancellationToken);
        }

        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            cancellationToken);

        return pipeline.ExecuteStepSequentiallyAsync($"build-{resource.Name}", context);
    }
}
