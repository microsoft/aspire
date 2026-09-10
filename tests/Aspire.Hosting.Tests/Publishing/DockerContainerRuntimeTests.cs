// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.Collection(
            processRunner.ProcessSpecs,
            check => Assert.Equal("buildx version", check.Arguments),
            create => Assert.Equal(
                "buildx create --name \"myapp-latest-builder\" --driver docker-container",
                create.Arguments),
            build =>
            {
                var arguments = Assert.IsType<string>(build.Arguments);
                Assert.Contains("--builder \"myapp-latest-builder\"", arguments, StringComparison.Ordinal);
                Assert.Contains($"--output \"{outputType},dest={archivePath}\"", arguments, StringComparison.Ordinal);
            },
            remove => Assert.Equal("buildx rm \"myapp-latest-builder\"", remove.Arguments));
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
            build =>
            {
                var arguments = Assert.IsType<string>(build.Arguments);
                Assert.DoesNotContain("--builder", arguments, StringComparison.Ordinal);
                Assert.Contains("--output \"type=docker\"", arguments, StringComparison.Ordinal);
            });
    }
}
