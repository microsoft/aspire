// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

public class PodmanSaveArgumentsTests
{
    [Fact]
    public void BuildSaveArguments_OciFormat_UsesOciArchive()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = Path.Combine("out", "archives"),
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options);

        var expectedPath = Path.Combine("out", "archives", "myapp-latest.tar");
        Assert.Equal($"save --format \"oci-archive\" --output \"{expectedPath}\" \"myapp:latest\"", arguments);
    }

    [Fact]
    public void BuildSaveArguments_DockerFormat_UsesDockerArchive()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = Path.Combine("out", "archives"),
            ImageFormat = ContainerImageFormat.Docker
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options);

        var expectedPath = Path.Combine("out", "archives", "myapp-latest.tar");
        Assert.Equal($"save --format \"docker-archive\" --output \"{expectedPath}\" \"myapp:latest\"", arguments);
    }

    [Fact]
    public void BuildSaveArguments_UnspecifiedFormat_DefaultsToDockerArchive()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = Path.Combine("out", "archives")
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options);

        var expectedPath = Path.Combine("out", "archives", "myapp-latest.tar");
        Assert.Equal($"save --format \"docker-archive\" --output \"{expectedPath}\" \"myapp:latest\"", arguments);
    }

    [Fact]
    public void BuildSaveArguments_ArchivePathMatchesDockerRuntime()
    {
        // Both runtimes must write the same archive file for the same resource, otherwise consumers that
        // resolve the archive path cannot find what Podman produced.
        var outputPath = Path.Combine("out", "archives");
        var options = new ContainerImageBuildOptions
        {
            ImageName = "registry.example.com/team/myapp",
            Tag = "1.2.3",
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("registry.example.com/team/myapp:1.2.3", options);

        var dockerArchivePath = ResourceExtensions.GetContainerImageArchivePath(outputPath, "registry.example.com/team/myapp:1.2.3");
        Assert.Contains($"--output \"{dockerArchivePath}\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSaveArguments_ArchivePathMatchesContainerImageReference()
    {
        // The runtimes write the archive, but ContainerImageReference is what tells consumers where to
        // find it, so the producer and the consumer have to resolve the same file. A registry-qualified
        // image name is the case where they used to diverge: the runtimes flattened the repository
        // segments while ContainerImageReference left the '/' in place, pointing consumers at a nested
        // path nothing ever wrote.
        var outputPath = Path.Combine("out", "archives");
        const string ImageName = "registry.example.com/team/myapp";
        const string ImageTag = "1.2.3";

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var container = builder.AddContainer("myapp", ImageName)
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = outputPath;
                ctx.LocalImageName = ImageName;
                ctx.LocalImageTag = ImageTag;
            });

        using var app = builder.Build();

        IValueProvider imageReference = new ContainerImageReference(container.Resource);
        var consumerPath = await imageReference.GetValueAsync(
            new ValueProviderContext { ExecutionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>() },
            CancellationToken.None);

        var options = new ContainerImageBuildOptions
        {
            ImageName = ImageName,
            Tag = ImageTag,
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments($"{ImageName}:{ImageTag}", options);

        Assert.Equal(
            $"save --format \"oci-archive\" --output \"{consumerPath}\" \"{ImageName}:{ImageTag}\"",
            arguments);
    }

    [Fact]
    public void BuildSaveArguments_TagIsPreservedInArchiveName()
    {
        // Dropping the tag would place the archive at a path nothing else resolves to.
        var options = new ContainerImageBuildOptions
        {
            ImageName = "container",
            Tag = "abc123",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        var arguments = PodmanContainerRuntime.BuildSaveArguments("container:abc123", options);

        Assert.Contains($"--output \"{Path.Combine("out", "container-abc123.tar")}\"", arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BuildImageAsync_OciFormatWithoutOutputPath_ThrowsBeforeBuild(string? outputPath)
    {
        var processRunner = new TestProcessRunner();
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Oci
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => BuildImageAsync(runtime, options));

        Assert.Equal("options", exception.ParamName);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BuildImageAsync_WithoutOutputPath_DoesNotSave(string? outputPath)
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = outputPath,
            ImageFormat = ContainerImageFormat.Docker
        };

        await BuildImageAsync(runtime, options);

        var buildSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.StartsWith("build ", Assert.IsType<string>(buildSpec.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildImageAsync_WithOutputPath_BuildsThenSaves()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        await BuildImageAsync(runtime, options);

        Assert.Collection(
            processRunner.ProcessSpecs,
            buildSpec => Assert.StartsWith("build ", Assert.IsType<string>(buildSpec.Arguments), StringComparison.Ordinal),
            saveSpec => Assert.Equal(PodmanContainerRuntime.BuildSaveArguments("myapp:latest", options), saveSpec.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_WhenBuildFails_DoesNotSave()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 17, output: ["build failed"]);
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => BuildImageAsync(runtime, options));

        Assert.Equal(17, exception.ExitCode);
        Assert.Contains("build failed", exception.Message);
        Assert.Single(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_WhenSaveFails_ThrowsProcessFailedException()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult(exitCode: 23, output: ["save failed"]);
        var runtime = CreateRuntime(processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => BuildImageAsync(runtime, options));

        Assert.Equal(23, exception.ExitCode);
        Assert.Contains("save failed", exception.Message);
        Assert.Collection(
            processRunner.ProcessSpecs,
            buildSpec => Assert.StartsWith("build ", Assert.IsType<string>(buildSpec.Arguments), StringComparison.Ordinal),
            saveSpec => Assert.StartsWith("save ", Assert.IsType<string>(saveSpec.Arguments), StringComparison.Ordinal));
    }

    private static PodmanContainerRuntime CreateRuntime(TestProcessRunner processRunner)
        => new(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

    private static Task BuildImageAsync(PodmanContainerRuntime runtime, ContainerImageBuildOptions options)
        => runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);
}
