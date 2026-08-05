// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Text.Json;
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
    public async Task InspectImageCommandsEscapeQuotesInImageName()
    {
        var processRunner = new CapturingProcessRunner();
        var runtime = new TestContainerRuntime(processRunner);

        await runtime.InspectImageConfigAsync("registry/image\" --help", TestContext.Current.CancellationToken);
        await runtime.InspectImageManifestAsync("registry/image\" --help", TestContext.Current.CancellationToken);

        Assert.Collection(
            processRunner.Arguments,
            arguments => Assert.Equal("image inspect \"registry/image\\\" --help\" --format \"{{json .Config}}\"", arguments),
            arguments => Assert.Equal("manifest inspect --verbose \"registry/image\\\" --help\"", arguments));
    }

    [Fact]
    public async Task PodmanInspectsRemoteImageManifestsUsingRegistryTransport()
    {
        var processRunner = new CapturingProcessRunner();
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        await runtime.InspectImageManifestAsync("registry/image\" --help", TestContext.Current.CancellationToken);
        await runtime.InspectImageManifestAsync("docker://registry/image:tag", TestContext.Current.CancellationToken);

        Assert.Collection(
            processRunner.Arguments,
            arguments => Assert.Equal("manifest inspect \"docker://registry/image\\\" --help\"", arguments),
            arguments => Assert.Equal("manifest inspect \"docker://registry/image:tag\"", arguments));
    }

    [Fact]
    public async Task PodmanResolvesDigestForPlainSingleImageManifest()
    {
        var processRunner = new CapturingProcessRunner(
        [
            new ProcessResult(0,
            [
                """{ "schemaVersion": 2, "config": { "digest": "sha256:config" }, "layers": [] }"""
            ]),
            new ProcessResult(0,
            [
                """{ "Digest": "sha256:linux-amd64", "Os": "linux", "Architecture": "amd64" }"""
            ])
        ]);
        var runtime = new PodmanContainerRuntime(NullLogger<PodmanContainerRuntime>.Instance, processRunner);

        var manifest = await runtime.InspectImageManifestAsync(
            "docker://registry/image\" --help:tag",
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(manifest);
        var descriptor = document.RootElement.GetProperty("Descriptor");
        Assert.Equal("sha256:linux-amd64", descriptor.GetProperty("digest").GetString());
        Assert.Equal("linux", descriptor.GetProperty("platform").GetProperty("os").GetString());
        Assert.Equal("amd64", descriptor.GetProperty("platform").GetProperty("architecture").GetString());
        Assert.Collection(
            processRunner.Arguments,
            arguments => Assert.Equal("manifest inspect \"docker://registry/image\\\" --help:tag\"", arguments),
            arguments => Assert.Equal("image inspect --format \"{{json .}}\" \"registry/image\\\" --help:tag\"", arguments));
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

    private sealed class CapturingProcessRunner(IEnumerable<ProcessResult>? results = null) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results ?? []);

        public List<string?> Arguments { get; } = [];

        public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        {
            Arguments.Add(processSpec.Arguments);
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
