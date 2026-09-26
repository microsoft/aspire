// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

[Trait("Partition", "4")]
public class ContainerRuntimeBaseTests
{
    [Fact]
    public async Task ExecuteContainerCommandAsync_IncludesCapturedOutputInFailureMessage()
    {
        var runtime = new TestContainerRuntime();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.RunFailingCommandAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Test command failed with exit code 1.", exception.Message);
        Assert.Contains("stdout-final-line", exception.Message);
        Assert.Contains("stderr-final-line", exception.Message);
    }

    [Fact]
    public async Task ExecuteContainerCommandForOutputAsync_ReturnsStdoutOnly()
    {
        var runtime = new TestContainerRuntime();

        var output = await runtime.RunCommandForOutputAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("stdout-only", output);
    }

    [Fact]
    public async Task InspectImageCommandsPreserveUntrustedImageNameAsSingleArgument()
    {
        var processRunner = new CapturingProcessRunner();
        var runtime = new TestContainerRuntime(processRunner);
        const string imageName = "registry/image\\\" --help";

        await runtime.InspectImageConfigAsync(imageName, TestContext.Current.CancellationToken);
        await runtime.InspectImageManifestAsync(imageName, TestContext.Current.CancellationToken);

        Assert.Collection(
            processRunner.ArgumentLists,
            arguments =>
            {
                Assert.Equal(
                    [
                        "image",
                        "inspect",
                        imageName,
                        "--format",
                        """{"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},"WorkingDir":{{json .Config.WorkingDir}}}"""
                    ],
                    arguments);
            },
            arguments => Assert.Equal(["manifest", "inspect", "--verbose", imageName], arguments));
    }

    [Fact]
    public async Task DockerInspectionReturnsTypedResults()
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """{"Entrypoint":["dotnet","/app/app.dll"],"Cmd":["--urls"],"WorkingDir":"/app"}"""
            ]),
            new ProcessResult(0,
            [
                """
                [
                  {
                    "Descriptor": {
                      "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "platform": { "os": "linux", "architecture": "amd64" }
                    }
                  }
                ]
                """
            ])
        ]);
        var runtime = new TestContainerRuntime(processRunner);

        var configResult = await runtime.InspectImageConfigAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);
        var manifestResult = await runtime.InspectImageManifestAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, configResult.Status);
        Assert.True(configResult.TryGetConfig(out var config));
        Assert.Equal(["dotnet", "/app/app.dll"], config.Entrypoint);
        Assert.Equal(["--urls"], config.Command);
        Assert.Equal("/app", config.WorkingDirectory);
        Assert.Equal(ContainerImageInspectionStatus.Succeeded, manifestResult.Status);
        Assert.True(manifestResult.TryGetManifest("linux", "amd64", out var manifest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", manifest.Digest);
    }

    [Fact]
    public async Task ContainerRuntimeInspectionDefaultsToUnsupported()
    {
        IContainerRuntime runtime = new UnsupportedInspectionContainerRuntime();

        var configResult = await runtime.InspectImageConfigAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);
        var manifestResult = await runtime.InspectImageManifestAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Unsupported, configResult.Status);
        Assert.False(configResult.TryGetConfig(out _));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, manifestResult.Status);
        Assert.False(manifestResult.TryGetManifest("linux", "amd64", out _));
    }

    [Fact]
    public void ContainerRuntimeImplementationsCanCreateInspectionResults()
    {
        var config = new ContainerImageConfig(["dotnet"], ["app.dll"], "/app");
        var configResult = ContainerImageConfigInspectionResult.Success(config, """{"config":true}""");
        var configFailure = ContainerImageConfigInspectionResult.Failure("config failed", """{"error":true}""");
        var manifest = new ContainerImageManifest(
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "linux",
            "amd64");
        var manifestResult = ContainerImageManifestInspectionResult.Success([manifest], """{"manifest":true}""");
        var manifestFailure = ContainerImageManifestInspectionResult.Failure("manifest failed", """{"error":true}""");

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, configResult.Status);
        Assert.True(configResult.TryGetConfig(out var inspectedConfig));
        Assert.Same(config, inspectedConfig);
        Assert.Equal("""{"config":true}""", configResult.RawJson);
        Assert.Equal(ContainerImageInspectionStatus.Failed, configFailure.Status);
        Assert.Equal("config failed", configFailure.ErrorMessage);
        Assert.Equal("""{"error":true}""", configFailure.RawJson);
        Assert.False(configFailure.TryGetConfig(out _));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, ContainerImageConfigInspectionResult.Unsupported.Status);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, manifestResult.Status);
        Assert.True(manifestResult.TryGetManifest("LINUX", "AMD64", out var inspectedManifest));
        Assert.Same(manifest, inspectedManifest);
        Assert.Equal("""{"manifest":true}""", manifestResult.RawJson);
        Assert.Equal(ContainerImageInspectionStatus.Failed, manifestFailure.Status);
        Assert.Equal("manifest failed", manifestFailure.ErrorMessage);
        Assert.Equal("""{"error":true}""", manifestFailure.RawJson);
        Assert.False(manifestFailure.TryGetManifest("linux", "amd64", out _));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, ContainerImageManifestInspectionResult.Unsupported.Status);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("sha256:")]
    [InlineData("sha256:not-a-hex-digest")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ContainerImageManifestRejectsInvalidDigests(string digest)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ContainerImageManifest(digest, "linux", "amd64"));

        Assert.Equal("digest", exception.ParamName);
    }

    [Fact]
    public async Task DockerIgnoresManifestsWithInvalidDigests()
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """
                [
                  {
                    "Descriptor": {
                      "digest": "sha256:not-a-hex-digest",
                      "platform": { "os": "linux", "architecture": "amd64" }
                    }
                  }
                ]
                """
            ])
        ]);
        var runtime = new TestContainerRuntime(processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "example/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.False(result.TryGetManifest("linux", "amd64", out _));
    }

    [Fact]
    public async Task PodmanInspectsRemoteImageManifestsUsingRegistryTransport()
    {
        var processRunner = new CapturingProcessRunner();
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);
        const string maliciousImageName = "registry/image\\\" --help";

        await runtime.InspectImageManifestAsync(maliciousImageName, TestContext.Current.CancellationToken);
        await runtime.InspectImageManifestAsync("docker://registry/image:tag", TestContext.Current.CancellationToken);

        Assert.Collection(
            processRunner.ArgumentLists,
            arguments => Assert.Equal(["manifest", "inspect", $"docker://{maliciousImageName}"], arguments),
            arguments => Assert.Equal(["manifest", "inspect", "docker://registry/image:tag"], arguments));
    }

    [Fact]
    public async Task PodmanReturnsTypedManifestFromNativeIndex()
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """
                {
                  "manifests": [
                    {
                      "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "platform": { "os": "linux", "architecture": "amd64" }
                    },
                    {
                      "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                      "platform": { "os": "linux", "architecture": "arm64" }
                    }
                  ]
                }
                """
            ])
        ]);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "registry/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.True(result.TryGetManifest("linux", "amd64", out var manifest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", manifest.Digest);
        Assert.False(result.TryGetManifest("windows", "amd64", out _));
        Assert.Contains("\"manifests\"", result.RawJson);
    }

    [Fact]
    public async Task PodmanResolvesDigestForPlainSingleImageManifest()
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """{ "schemaVersion": 2, "config": { "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }, "layers": [] }"""
            ]),
            new ProcessResult(0),
            new ProcessResult(0,
            [
                """{ "Digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Os": "linux", "Architecture": "amd64" }"""
            ])
        ]);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);
        const string maliciousImageName = "registry/image\\\" --help:tag";

        var result = await runtime.InspectImageManifestAsync(
            $"docker://{maliciousImageName}",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.True(result.TryGetManifest("linux", "amd64", out var manifest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", manifest.Digest);
        Assert.Equal("linux", manifest.OperatingSystem);
        Assert.Equal("amd64", manifest.Architecture);
        Assert.Collection(
            processRunner.ArgumentLists,
            arguments => Assert.Equal(["manifest", "inspect", $"docker://{maliciousImageName}"], arguments),
            arguments => Assert.Equal(["pull", $"docker://{maliciousImageName}"], arguments),
            arguments => Assert.Equal(
                [
                    "image",
                    "inspect",
                    "--format",
                    """{"Digest":{{json .Digest}},"Os":{{json .Os}},"Architecture":{{json .Architecture}}}""",
                    maliciousImageName
                ],
                arguments));
    }

    [Fact]
    public async Task PodmanFailsInspectionWhenImageMetadataHasInvalidDigest()
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """{ "schemaVersion": 2, "config": { "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }, "layers": [] }"""
            ]),
            new ProcessResult(0),
            new ProcessResult(0,
            [
                """{ "Digest": "sha256:not-a-hex-digest", "Os": "linux", "Architecture": "amd64" }"""
            ])
        ]);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "registry/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.Equal("Podman did not return an immutable digest for image 'registry/image:tag'.", result.ErrorMessage);
        Assert.False(result.TryGetManifest("linux", "amd64", out _));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "Digest": 123, "Os": "linux", "Architecture": "amd64" }""")]
    public async Task PodmanFailsInspectionWhenImageMetadataIsMalformed(string imageMetadata)
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """{ "schemaVersion": 2, "config": { "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }, "layers": [] }"""
            ]),
            new ProcessResult(0),
            new ProcessResult(0, [imageMetadata])
        ]);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var result = await runtime.InspectImageManifestAsync(
            "registry/image:tag",
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.StartsWith("Podman returned invalid image metadata for 'registry/image:tag':", result.ErrorMessage);
        Assert.False(result.TryGetManifest("linux", "amd64", out _));
    }

    private sealed class TestContainerRuntime(IProcessRunner? processRunner = null, string? runtimeExecutable = null) : ContainerRuntimeBase<TestContainerRuntime>(NullLogger<TestContainerRuntime>.Instance, processRunner ?? new DefaultProcessRunner())
    {
        protected override string RuntimeExecutable => runtimeExecutable ?? (OperatingSystem.IsWindows() ? "cmd" : "sh");

        public override string Name => "test-runtime";

        public override Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public override Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task RunFailingCommandAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteContainerCommandAsync(
                OperatingSystem.IsWindows()
                    ? "/c \"echo stdout-final-line & echo stderr-final-line 1>&2 & exit /b 1\""
                    : "-c \"echo stdout-final-line; echo stderr-final-line 1>&2; exit 1\"",
                "Test command failed with exit code {ExitCode}.",
                "Test command succeeded.",
                "Test command failed with exit code {0}.",
                cancellationToken);
        }

        public Task<string> RunCommandForOutputAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteContainerCommandForOutputAsync(
                OperatingSystem.IsWindows()
                    ? "/c \"echo stdout-only& echo stderr-line 1>&2\""
                    : "-c \"echo stdout-only; echo stderr-line 1>&2\"",
                "test output",
                "test-image",
                cancellationToken);
        }
    }

    private sealed class UnsupportedInspectionContainerRuntime : IContainerRuntime
    {
        public string Name => "unsupported-inspection";

        public Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TagImageAsync(string localImageName, string targetImageName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveImageAsync(string imageName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PushImageAsync(IResource resource, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LoginToRegistryAsync(string registryServer, string username, string password, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ComposeServiceInfo>?>(null);
    }

    private sealed class CapturingProcessRunner(IEnumerable<ProcessResult>? results = null) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results ?? []);

        public List<IReadOnlyList<string>?> ArgumentLists { get; } = [];

        public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        {
            ArgumentLists.Add(processSpec.ArgumentList);
            var result = _results.Count > 0 ? _results.Dequeue() : new ProcessResult(0);
            foreach (var output in result.ProcessOutput)
            {
                processSpec.OnOutputData?.Invoke(output);
            }
            return (Task.FromResult(result), new NoOpAsyncDisposable());
        }

        private sealed class NoOpAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
