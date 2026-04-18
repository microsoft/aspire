// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

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
    public async Task LoginToRegistryAsync_IncludesCapturedOutputInFailureMessage()
    {
        var runtime = new TestContainerRuntime("dotnet");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.LoginToRegistryAsync("registry.example.com", "user", "password", default)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("test-runtime login failed with exit code 1.", exception.Message);
        Assert.Contains("Could not execute because the specified command or file was not found.", exception.Message);
        Assert.Contains("dotnet-login", exception.Message);
    }

    [Fact]
    public async Task ComposeUpAsync_IncludesCapturedOutputInFailureMessage()
    {
        var runtime = new TestContainerRuntime("dotnet");
        var context = new ComposeOperationContext
        {
            ProjectName = "test-project",
            WorkingDirectory = AppContext.BaseDirectory
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            runtime.ComposeUpAsync(context, default)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("'dotnet compose up' failed with exit code 1.", exception.Message);
        Assert.Contains("Could not execute because the specified command or file was not found.", exception.Message);
        Assert.Contains("dotnet-compose", exception.Message);
    }

    private sealed class TestContainerRuntime(string? runtimeExecutable = null) : ContainerRuntimeBase<TestContainerRuntime>(NullLogger<TestContainerRuntime>.Instance)
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
    }
}
