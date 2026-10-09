// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using System.Text.RegularExpressions;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

public class RemoteContainerImageTransferTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "sha256:1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string CapabilityHelp = "      --prefer-index             When only a single source is specified";

    [Theory]
    [InlineData(Digest, Digest, true)]
    [InlineData(OtherDigest, Digest, false)]
    [InlineData(Digest, OtherDigest, false)]
    public async Task ArtifactBuildVerifiesBuildMetadataAgainstTheCompleteLocalRoot(string metadataDigest, string localDigest, bool success)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: ["""[["driver-type","io.containerd.snapshotter.v1"]]"""]);
        runner.EnqueueResult(output: ["github.com/docker/buildx"]);
        runner.EnqueueResult(output: ["default"]);
        runner.EnqueueResult();
        runner.EnqueueResult(output: [localDigest]);
        string? metadataPath = null;
        runner.RunCallback = spec =>
        {
            if (spec.Arguments?.StartsWith("buildx build ", StringComparison.Ordinal) == true)
            {
                // The builder uses: --metadata-file "/secure/temp/directory/metadata.json".
                metadataPath = Regex.Match(spec.Arguments, "--metadata-file \"([^\"]+)\"").Groups[1].Value;
                File.WriteAllText(metadataPath, $$"""{"containerimage.digest":"{{metadataDigest}}"}""");
            }
        };
        IContainerImageArtifactRuntime runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        var build = runtime.BuildImageArtifactAsync(
            Path.GetFullPath("."), Path.GetFullPath("Dockerfile"),
            new() { ImageName = "tools", Tag = "build", TargetPlatform = ContainerTargetPlatform.AllLinux },
            [], [], "release", default);
        if (success)
        {
            Assert.Equal(Digest, await build);
        }
        else
        {
            await Assert.ThrowsAsync<DistributedApplicationException>(() => build);
        }
        Assert.NotNull(metadataPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(metadataPath)));
        var spec = runner.ProcessSpecs[3];
        Assert.Contains("--load", spec.Arguments!);
        Assert.Contains("--platform \"linux/amd64,linux/arm64\"", spec.Arguments!);
        Assert.Contains("--target \"release\"", spec.Arguments!);
        AssertCommand(runner.ProcessSpecs[4], ["image", "inspect", "tools:build", "--format", "{{.Descriptor.Digest}}"]);
    }

    [Theory]
    [InlineData("""[["Backing Filesystem","extfs"]]""")]
    [InlineData("null")]
    [InlineData("not-json")]
    public async Task ArtifactBuildRejectsStoresThatCannotRetainTheGraphBeforeBuilding(string status)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [status]);
        IContainerImageArtifactRuntime runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.BuildImageArtifactAsync(
            ".", "Dockerfile", new() { ImageName = "tools", Tag = "build" }, [], [], null, default));
        AssertCommand(Assert.Single(runner.ProcessSpecs), ["info", "--format", "{{json .DriverStatus}}"]);
    }

    [Theory]
    [InlineData(Digest, true)]
    [InlineData(OtherDigest, false)]
    public async Task ArtifactPublicationTagsTheImmutableIndexAndVerifiesItsDestination(string publishedDigest, bool success)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult();
        runner.EnqueueResult();
        runner.EnqueueResult(output: [publishedDigest]);
        IContainerImageArtifactRuntime runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        var publication = runtime.PublishImageArtifactAsync(Digest, "localhost:5000/tools:publication", default);
        if (success)
        {
            Assert.Equal("localhost:5000/tools@" + Digest, await publication);
        }
        else
        {
            await Assert.ThrowsAsync<DistributedApplicationException>(() => publication);
        }
        Assert.Collection(runner.ProcessSpecs,
            spec => AssertCommand(spec, ["tag", Digest, "localhost:5000/tools:publication"]),
            spec => AssertCommand(spec, ["push", "localhost:5000/tools:publication"]),
            spec => AssertCommand(spec, ["buildx", "imagetools", "inspect", "localhost:5000/tools:publication", "--format", "{{.Manifest.Digest}}"]));
    }

    [Theory]
    [InlineData("busybox", "docker.io/library/busybox:latest", "docker.io/library/busybox@" + Digest)]
    [InlineData("ghcr.io/team/tools:v1", "ghcr.io/team/tools:v1", "ghcr.io/team/tools@" + Digest)]
    [InlineData("localhost:5000/team/tools:v1", "localhost:5000/team/tools:v1", "localhost:5000/team/tools@" + Digest)]
    [InlineData("[::1]:5000/tools:v1", "[::1]:5000/tools:v1", "[::1]:5000/tools@" + Digest)]
    [InlineData("busybox@" + Digest, "docker.io/library/busybox@" + Digest, "docker.io/library/busybox@" + Digest)]
    [InlineData("busybox:moving@" + Digest, "docker.io/library/busybox@" + Digest, "docker.io/library/busybox@" + Digest)]
    public async Task ResolvePinsTheRootContentWithoutSelectingAPlatform(string source, string inspectedReference, string expected)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult(output: [Digest + "\n"]);
        IContainerRuntime runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        Assert.Equal(expected, await runtime.ResolveRemoteImageAsync(source, default));
        Assert.Collection(runner.ProcessSpecs,
            spec => AssertCommand(spec, ["buildx", "imagetools", "create", "--help"]),
            spec => AssertCommand(spec, ["buildx", "imagetools", "inspect", inspectedReference, "--format", "{{.Manifest.Digest}}"]));
        Assert.All(runner.Disposables, disposable => Assert.Equal(1, disposable.DisposeCallCount));
    }

    [Fact]
    public async Task CopyUsesImmutableSourceAndVerifiesThePublicationTag()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult();
        runner.EnqueueResult(output: [Digest]);
        IContainerRuntime runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        var destination = await runtime.CopyRemoteImageAsync(
            "ghcr.io/team/tools:ignored@" + Digest, "localhost:5000/team/published:v1", default);

        Assert.Equal("localhost:5000/team/published@" + Digest, destination);
        Assert.Collection(runner.ProcessSpecs,
            spec => AssertCommand(spec, ["buildx", "imagetools", "create", "--help"]),
            spec => AssertCommand(spec,
                ["buildx", "imagetools", "create", "--prefer-index=false", "--tag", "localhost:5000/team/published:v1", "ghcr.io/team/tools@" + Digest]),
            spec => AssertCommand(spec,
                ["buildx", "imagetools", "inspect", "localhost:5000/team/published:v1", "--format", "{{.Manifest.Digest}}"]));
        Assert.All(runner.Disposables, disposable => Assert.Equal(1, disposable.DisposeCallCount));
    }

    [Fact]
    public async Task FanOutReusesOnePreparedSourceRatherThanResolvingItsTagAgain()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult(output: [Digest]);
        for (var i = 0; i < 2; i++)
        {
            runner.EnqueueResult(output: [CapabilityHelp]);
            runner.EnqueueResult();
            runner.EnqueueResult(output: [Digest]);
        }
        IContainerRuntime runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        var source = await runtime.ResolveRemoteImageAsync("busybox:latest", default);
        var first = await runtime.CopyRemoteImageAsync(source, "first.example.com/tools:publication", default);
        var second = await runtime.CopyRemoteImageAsync(source, "second.example.com/tools:publication", default);

        Assert.Equal("first.example.com/tools@" + Digest, first);
        Assert.Equal("second.example.com/tools@" + Digest, second);
        Assert.Equal(
            [source, source],
            runner.ProcessSpecs.Where(spec => spec.ArgumentList?.Contains("--tag") == true)
                .Select(spec => spec.ArgumentList![^1]).ToArray());
        Assert.Equal(
            ["docker.io/library/busybox:latest", "first.example.com/tools:publication", "second.example.com/tools:publication"],
            runner.ProcessSpecs.Where(spec => spec.ArgumentList?[2] == "inspect")
                .Select(spec => spec.ArgumentList![3]).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<no value>")]
    [InlineData("sha256:1234")]
    [InlineData(Digest + "\n" + Digest)]
    [InlineData("user:password")]
    public async Task InvalidDigestOutputFailsWithoutEchoingRawOutput(string output)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult(output: [output]);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ResolveRemoteImageAsync("busybox", default));
        Assert.Equal("Docker returned an invalid root manifest digest for 'docker.io/library/busybox:latest'.", exception.Message);
        Assert.Equal(2, runner.ProcessSpecs.Count);
    }

    [Fact]
    public async Task PinnedSourceMustMatchTheResolvedDigest()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult(output: [OtherDigest]);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.ResolveRemoteImageAsync("busybox@" + Digest, default));
        Assert.Equal("Docker reported a different content digest for pinned source image 'docker.io/library/busybox@" + Digest + "'.", exception.Message);
    }

    [Theory]
    [InlineData("sha256", 64)]
    [InlineData("sha384", 96)]
    [InlineData("sha512", 128)]
    public async Task CopyPreservesTheCompleteContentDigest(string algorithm, int length)
    {
        var digest = $"{algorithm}:{new string('a', length)}";
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult();
        runner.EnqueueResult(output: [digest]);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        Assert.Equal("registry.example.com/tools@" + digest,
            await runtime.CopyRemoteImageAsync("busybox@" + digest, "registry.example.com/tools:v1", default));
    }

    [Fact]
    public async Task DestinationDigestMismatchIsNotReportedAsSuccessfulPublication()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueueResult();
        runner.EnqueueResult(output: [OtherDigest]);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:publication", default));
        Assert.Equal("Destination image 'registry.example.com/tools:publication' does not have the source content digest. Remote image publication could not be verified.", exception.Message);
    }

    [Theory]
    [InlineData("busybox:latest", "registry.example.com/tools:v1")]
    [InlineData("busybox", "registry.example.com/tools:v1")]
    [InlineData("busybox@" + Digest, "registry.example.com/tools")]
    [InlineData("busybox@" + Digest, "tools:v1")]
    [InlineData("busybox@" + Digest, "team/tools:v1")]
    [InlineData("busybox@" + Digest, "registry.example.com/tools@" + Digest)]
    [InlineData("busybox@" + Digest, "registry.example.com/tools:v1@" + Digest)]
    [InlineData("busybox@" + Digest, "https://user:password@registry.example.com/tools:v1")]
    [InlineData("busybox@" + Digest, "registry.example.com/tools:\";other-command")]
    public async Task InvalidTransferReferencesAreRejectedBeforeExecutingCommands(string source, string destination)
    {
        var runner = new TestProcessRunner();
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => runtime.CopyRemoteImageAsync(source, destination, default));
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("      --prefer-index-other")]
    [InlineData("Docker Buildx v0.9.0")]
    public async Task MissingLosslessCopyFlagFailsBeforeAccessingAnyImage(string help)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [help]);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", default));
        Assert.Equal(
            "Docker Buildx cannot preserve remote image manifests without --prefer-index=false. " +
            "Install a Buildx version supporting that flag: https://docs.docker.com/build/install-buildx/.", exception.Message);
        AssertCommand(Assert.Single(runner.ProcessSpecs), ["buildx", "imagetools", "create", "--help"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedCommandsSurfaceDiagnosticsAndPreventSubsequentCommands(int failingCommand)
    {
        var runner = new TestProcessRunner();
        if (failingCommand > 0)
        {
            runner.EnqueueResult(output: [CapabilityHelp]);
        }
        if (failingCommand > 1)
        {
            runner.EnqueueResult();
        }
        runner.EnqueueResult(exitCode: 42, error: ["denied: registry access forbidden"]);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", default));
        Assert.Contains("exit code 42", exception.Message);
        Assert.Contains("denied: registry access forbidden", exception.Message);
        Assert.Equal(failingCommand + 1, runner.ProcessSpecs.Count);
        Assert.All(runner.Disposables, disposable => Assert.Equal(1, disposable.DisposeCallCount));
    }

    [Fact]
    public async Task CancellationDuringCopyDisposesTheProcessWithoutVerifyingOrRetrying()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        runner.EnqueuePending(new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        using var cancellation = new CancellationTokenSource();
        var copy = runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.Equal(2, runner.ProcessSpecs.Count);
        Assert.All(runner.Disposables, disposable => Assert.Equal(1, disposable.DisposeCallCount));
    }

    [Fact]
    public async Task AlreadyCancelledOperationsDoNotStartProcesses()
    {
        var runner = new TestProcessRunner();
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ResolveRemoteImageAsync("busybox", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.BuildImageArtifactAsync(
            ".", "Dockerfile", new() { ImageName = "tools", Tag = "build" }, [], [], null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.PublishImageArtifactAsync(Digest, "registry.example.com/tools:v1", cancellation.Token));
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task CancellationAfterCapabilityProbePreventsRegistryMutation()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [CapabilityHelp]);
        using var cancellation = new CancellationTokenSource();
        runner.RunCallback = _ => cancellation.Cancel();
        var runtime = new DockerContainerRuntime(NullLogger<DockerContainerRuntime>.Instance, runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", cancellation.Token));
        AssertCommand(Assert.Single(runner.ProcessSpecs), ["buildx", "imagetools", "create", "--help"]);
        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);
    }

    [Fact]
    public async Task CustomRuntimeWithoutNewCapabilitiesKeepsExistingBehavior()
    {
        var fake = new FakeContainerRuntime();
        IContainerRuntime runtime = fake;

        var resolve = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.ResolveRemoteImageAsync("busybox", default));
        Assert.Equal("Container runtime 'fake-runtime' does not support remote image resolution. Use Docker with Buildx.", resolve.Message);
        var copy = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", default));
        Assert.Equal("Container runtime 'fake-runtime' does not support lossless remote image copying. Use Docker with Buildx.", copy.Message);

        await runtime.TagImageAsync("local:latest", "registry.example.com/compute:latest", default);
        Assert.Equal([("local:latest", "registry.example.com/compute:latest")], fake.TagImageCalls);
        Assert.Empty(fake.PushImageCalls);
        Assert.Empty(fake.BuildImageCalls);
    }

    [Fact]
    public async Task PodmanFailsExplicitlyRatherThanFallingBackToPlatformSelectedPullAndPush()
    {
        var runner = new TestProcessRunner();
        IContainerRuntime runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, runner);

        var resolve = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ResolveRemoteImageAsync("busybox", default));
        Assert.Equal("Container runtime 'Podman' does not support remote image resolution. Use Docker with Buildx.", resolve.Message);
        var copy = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.CopyRemoteImageAsync("busybox@" + Digest, "registry.example.com/tools:v1", default));
        Assert.Equal("Container runtime 'Podman' does not support lossless remote image copying. Use Docker with Buildx.", copy.Message);
        Assert.Empty(runner.ProcessSpecs);
    }

    private static void AssertCommand(ProcessSpec spec, string[] expected)
    {
        Assert.Equal("docker", spec.ExecutablePath);
        Assert.Equal(expected, spec.ArgumentList);
        Assert.Null(spec.Arguments);
        Assert.True(spec.InheritEnv);
        Assert.False(spec.ThrowOnNonZeroReturnCode);
    }
}
