// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests.Publishing;

public class DockerContainerRuntimeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ContainerImageFormat.Docker)]
    [InlineData(ContainerImageFormat.Oci)]
    public async Task BuildImageAsync_ArchiveUsesIsolatedBuilder(ContainerImageFormat? imageFormat)
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = imageFormat
        };

        await runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);

        var archivePath = ResourceExtensions.GetContainerImageArchivePath("out", "myapp:latest");
        var outputType = imageFormat == ContainerImageFormat.Oci ? "type=oci" : "type=docker";
        var processSpecs = processRunner.ProcessSpecs;
        Assert.Equal(4, processSpecs.Count);
        Assert.Equal("buildx version", processSpecs[0].Arguments);
        var builderName = AssertAndGetBuilderName(processSpecs[1].Arguments);
        Assert.Equal(
            $"buildx build --file \"Dockerfile\" --tag \"myapp:latest\" --builder \"{builderName}\" " +
            $"--output \"{outputType},dest={archivePath}\" \"{GetNormalizedContextPath()}\"",
            processSpecs[2].Arguments);
        Assert.Equal($"buildx rm \"{builderName}\"", processSpecs[3].Arguments);
    }

    [Fact]
    public async Task BuildImageAsync_ArchiveUsesUniqueIsolatedBuilders()
    {
        var processRunner = new TestProcessRunner();
        for (var i = 0; i < 8; i++)
        {
            processRunner.EnqueueResult();
        }

        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker
        };

        await BuildImageAsync(runtime, options);
        await BuildImageAsync(runtime, options);

        var builderNames = processRunner.ProcessSpecs
            .Where(static spec => spec.Arguments is string arguments &&
                arguments.StartsWith("buildx create ", StringComparison.Ordinal))
            .Select(spec => AssertAndGetBuilderName(spec.Arguments))
            .ToArray();
        Assert.Equal(2, builderNames.Length);
        Assert.Equal(2, builderNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task BuildImageAsync_CancellationWaitsForIsolatedBuilderCleanup()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        var buildResult = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupResult = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        processRunner.EnqueuePending(buildResult.Task);
        processRunner.EnqueuePending(cleanupResult.Task);
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker
        };
        using var cancellation = new CancellationTokenSource();

        var buildTask = runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: cancellation.Token);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => processRunner.ProcessSpecs.Count == 3,
            "The Docker build should start.");
        await cancellation.CancelAsync();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => processRunner.ProcessSpecs.Count == 4,
            "The isolated builder cleanup should start after cancellation.");

        var builderName = AssertAndGetBuilderName(processRunner.ProcessSpecs[1].Arguments);
        Assert.Equal($"buildx rm \"{builderName}\"", processRunner.ProcessSpecs[3].Arguments);
        try
        {
            Assert.False(buildTask.IsCompleted);
        }
        finally
        {
            cleanupResult.TrySetResult(new ProcessResult(0));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask).DefaultTimeout();
    }

    [Fact]
    public async Task BuildImageAsync_CleanupFailureDoesNotMaskBuildFailure()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult(exitCode: 42, output: ["build failed"]);
        processRunner.EnqueueException(new InvalidOperationException("cleanup failed"));
        var logger = new FakeLogger<DockerContainerRuntime>();
        var runtime = new DockerContainerRuntime(logger, processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Docker
        };

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal(42, exception.ExitCode);
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            log => log.Level == LogLevel.Warning &&
                log.Message.Contains("Failed to remove buildkit instance aspire-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ContainerImageFormat.Docker)]
    public async Task BuildImageAsync_LocalImageArchiveUsesDefaultBuilderThenDockerSave(ContainerImageFormat? imageFormat)
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = imageFormat,
            TargetPlatform = ContainerTargetPlatform.LinuxAmd64,
            RequiresLocalImageStore = true
        };

        await BuildImageAsync(runtime, options);

        var archivePath = ResourceExtensions.GetContainerImageArchivePath("out", "myapp:latest");
        Assert.Collection(
            processRunner.ProcessSpecs,
            check => Assert.Equal("buildx version", check.Arguments),
            build => Assert.Equal(
                $"buildx build --file \"Dockerfile\" --tag \"myapp:latest\" --builder \"default\" " +
                $"--platform \"linux/amd64\" \"{GetNormalizedContextPath()}\"",
                build.Arguments),
            save => Assert.Equal(
                $"image save --output \"{archivePath}\" --platform \"linux/amd64\" \"myapp:latest\"",
                save.Arguments));
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageOciArchiveFailsBeforeInvokingDocker()
    {
        var processRunner = new TestProcessRunner();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = ContainerImageFormat.Oci,
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal(
            "Docker cannot export an OCI archive when container-file layering references locally built images. " +
            "Use ContainerImageFormat.Docker for this archive or run the publish with Podman.",
            exception.Message);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_LocalImageArchiveRejectsInvalidFormat()
    {
        var processRunner = new TestProcessRunner();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            OutputPath = "out",
            ImageFormat = (ContainerImageFormat)42,
            RequiresLocalImageStore = true
        };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BuildImageAsync(runtime, options));

        Assert.Equal("options", exception.ParamName);
        Assert.Empty(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_WithoutArchiveUsesDefaultBuilder()
    {
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        var runtime = new DockerContainerRuntime(
            NullLogger<DockerContainerRuntime>.Instance,
            processRunner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "myapp",
            Tag = "latest",
            ImageFormat = ContainerImageFormat.Docker
        };

        await runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);

        Assert.Collection(
            processRunner.ProcessSpecs,
            check => Assert.Equal("buildx version", check.Arguments),
            build => Assert.Equal(
                $"buildx build --file \"Dockerfile\" --tag \"myapp:latest\" " +
                $"--output \"type=docker\" \"{GetNormalizedContextPath()}\"",
                build.Arguments));
    }

    private static Task BuildImageAsync(
        DockerContainerRuntime runtime,
        ContainerImageBuildOptions options)
    {
        return runtime.BuildImageAsync(
            contextPath: "context",
            dockerfilePath: "Dockerfile",
            options: options,
            buildArguments: [],
            buildSecrets: [],
            stage: null,
            cancellationToken: CancellationToken.None);
    }

    private static string AssertAndGetBuilderName(object? argumentsValue)
    {
        var arguments = Assert.IsType<string>(argumentsValue);
        const string prefix = "buildx create --name \"";
        const string suffix = "\" --driver docker-container";
        Assert.StartsWith(prefix, arguments, StringComparison.Ordinal);
        Assert.EndsWith(suffix, arguments, StringComparison.Ordinal);
        var builderName = arguments[prefix.Length..^suffix.Length];
        Assert.StartsWith("aspire-", builderName, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(builderName["aspire-".Length..], "N", out _));
        return builderName;
    }

    private static string GetNormalizedContextPath()
    {
        return Path.GetFullPath("context")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
