// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Publishing;

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

        var dockerArchivePath = DockerContainerRuntime.GetArchivePath(outputPath, "registry.example.com/team/myapp:1.2.3");
        Assert.Contains($"--output \"{dockerArchivePath}\"", arguments, StringComparison.Ordinal);
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
}
