// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class DotnetSdkVersionProviderTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("10.0.999", false)]
    [InlineData("11.0.100-preview.7.25380.108", false)]
    [InlineData("11.0.100-beta.1", false)]
    [InlineData("11.0.100-rc.0.1", false)]
    [InlineData("11.0.100-rc.1", true)]
    [InlineData("11.0.100-rc.1.26425.128", true)]
    [InlineData("11.0.100-rc.2.1", true)]
    [InlineData("11.0.100", true)]
    [InlineData("11.0.101-servicing.1", true)]
    [InlineData("11.0.200-preview.1", true)]
    [InlineData("12.0.100-preview.1", true)]
    [InlineData("12.0.100-beta.1", true)]
    [InlineData("12.0.100-rc.1", true)]
    [InlineData("12.0.100", true)]
    [InlineData("13.0.100-alpha.1", true)]
    [InlineData("11.0.100-rc.1+build.42", true)]
    public void SupportsMultiThreadedBuildUsesSemVersionPrecedence(string? version, bool expected)
    {
        var parsedVersion = version is null
            ? null
            : SemVersion.Parse(version, SemVersionStyles.Strict);

        Assert.Equal(expected, DotnetSdkUtils.SupportsMultiThreadedBuild(parsedVersion));
    }

    [Fact]
    public async Task TryGetVersionAsyncRunsQuietVersionProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["11.0.100-rc.1.26425.128"]);
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100-rc.1.26425.128", version?.ToString());
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("dotnet", processSpec.ExecutablePath);
        Assert.Equal(workspace.Path, processSpec.WorkingDirectory);
        Assert.Equal(["--version"], processSpec.ArgumentList);
        Assert.True(processSpec.ResolveExecutablePath);
        Assert.False(processSpec.ThrowOnNonZeroReturnCode);
        Assert.Equal("true", processSpec.EnvironmentVariables["DOTNET_NOLOGO"]);
        Assert.Equal("true", processSpec.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public async Task SupportsMultiThreadedBuildAsyncUsesBuildEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["11.0.100-rc.1"]);
        var provider = CreateProvider(processRunner);
        var buildEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "custom-dotnet-path",
            ["DOTNET_NOLOGO"] = "false",
        };

        var supported = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            buildEnvironment,
            TestContext.Current.CancellationToken);

        Assert.True(supported);
        var processSpec = Assert.Single(processRunner.ProcessSpecs);
        Assert.Equal("custom-dotnet-path", processSpec.EnvironmentVariables["PATH"]);
        Assert.Equal("false", processSpec.EnvironmentVariables["DOTNET_NOLOGO"]);
        Assert.Equal("true", processSpec.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public async Task SupportsMultiThreadedBuildAsyncCachesByEnvironmentContents()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["11.0.100-rc.1"]);
        processRunner.EnqueueResult(output: ["10.0.999"]);
        var provider = CreateProvider(processRunner);
        var firstEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "first-dotnet-path",
            ["BUILD_FLAVOR"] = "custom",
        };
        var equivalentEnvironment = new Dictionary<string, string>
        {
            ["BUILD_FLAVOR"] = "custom",
            ["PATH"] = "first-dotnet-path",
        };
        var changedEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "second-dotnet-path",
            ["BUILD_FLAVOR"] = "custom",
        };

        var first = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            firstEnvironment,
            TestContext.Current.CancellationToken);
        var equivalent = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            equivalentEnvironment,
            TestContext.Current.CancellationToken);
        var changed = await provider.SupportsMultiThreadedBuildAsync(
            workspace.Path,
            changedEnvironment,
            TestContext.Current.CancellationToken);

        Assert.True(first);
        Assert.True(equivalent);
        Assert.False(changed);
        Assert.Collection(
            processRunner.ProcessSpecs,
            processSpec => Assert.Equal(
                "first-dotnet-path",
                processSpec.EnvironmentVariables["PATH"]),
            processSpec => Assert.Equal(
                "second-dotnet-path",
                processSpec.EnvironmentVariables["PATH"]));
    }

    [Fact]
    public async Task TryGetVersionAsyncCoalescesConcurrentRequests()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processRunner.EnqueuePending(processCompletion.Task);
        var provider = CreateProvider(processRunner);

        var first = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        await processRunner.RunStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Single(processRunner.ProcessSpecs);

        processCompletion.SetResult(new ProcessResult(0, ["11.0.100-rc.1"]));
        var versions = await Task.WhenAll(first, second);

        Assert.All(versions, version => Assert.Equal("11.0.100-rc.1", version?.ToString()));
        Assert.Single(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task TryGetVersionAsyncSharesNearestGlobalJsonContext()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "global.json"), "{}");
        var firstDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "First")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "Second")).FullName;
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["11.0.100"]);
        var provider = CreateProvider(processRunner);

        var first = await provider.TryGetVersionAsync(
            firstDirectory,
            TestContext.Current.CancellationToken);
        var second = await provider.TryGetVersionAsync(
            secondDirectory,
            TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Single(processRunner.ProcessSpecs);
    }

    [Fact]
    public async Task TryGetVersionAsyncSeparatesDifferentGlobalJsonContexts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var firstDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "First")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "Second")).FullName;
        File.WriteAllText(Path.Combine(firstDirectory, "global.json"), "{}");
        File.WriteAllText(Path.Combine(secondDirectory, "global.json"), "{}");
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["11.0.100"]);
        processRunner.EnqueueResult(output: ["12.0.100-preview.1"]);
        var provider = CreateProvider(processRunner);

        var first = await provider.TryGetVersionAsync(
            firstDirectory,
            TestContext.Current.CancellationToken);
        var second = await provider.TryGetVersionAsync(
            secondDirectory,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100", first?.ToString());
        Assert.Equal("12.0.100-preview.1", second?.ToString());
        Assert.Equal(2, processRunner.ProcessSpecs.Count);
    }

    [Fact]
    public async Task TryGetVersionAsyncRefreshesWhenGlobalJsonChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var globalJsonPath = Path.Combine(workspace.Path, "global.json");
        File.WriteAllText(globalJsonPath, "{}");
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["11.0.100"]);
        processRunner.EnqueueResult(output: ["10.0.100"]);
        var provider = CreateProvider(processRunner);

        var first = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        File.WriteAllText(globalJsonPath, """{"sdk":{"version":"10.0.100"}}""");
        var second = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100", first?.ToString());
        Assert.Equal("10.0.100", second?.ToString());
        Assert.Equal(2, processRunner.ProcessSpecs.Count);
    }

    [Fact]
    public async Task TryGetVersionAsyncRetriesAfterFailedProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 1);
        processRunner.EnqueueResult(output: ["11.0.100-rc.1"]);
        var provider = CreateProvider(processRunner);

        var failed = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        var retried = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Null(failed);
        Assert.Equal("11.0.100-rc.1", retried?.ToString());
        Assert.Equal(2, processRunner.ProcessSpecs.Count);
    }

    [Fact]
    public async Task TryGetVersionAsyncReturnsNullForMalformedOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(output: ["not-a-version"]);
        var provider = CreateProvider(processRunner);

        var version = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Null(version);
    }

    [Fact]
    public async Task CanceledConsumerDoesNotRetainFailedProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processRunner.EnqueuePending(processCompletion.Task);
        var provider = CreateProvider(processRunner);
        using var callerCancellationSource = new CancellationTokenSource();

        var canceledCall = provider.TryGetVersionAsync(
            workspace.Path,
            callerCancellationSource.Token);
        await processRunner.RunStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var sharedCall = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        callerCancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        processCompletion.SetResult(new ProcessResult(1));
        Assert.Null(await sharedCall);
        Assert.Equal(1, Assert.Single(processRunner.Disposables).DisposeCallCount);

        processRunner.EnqueueResult(output: ["11.0.100-rc.1"]);
        var retried = await provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.100-rc.1", retried?.ToString());
        Assert.Equal(2, processRunner.ProcessSpecs.Count);
    }

    [Fact]
    public async Task ApplicationStoppingCancelsProbeAndDisposesProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processRunner = new TestProcessRunner();
        var processCompletion = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        processRunner.EnqueuePending(processCompletion.Task);
        using var applicationStoppingSource = new CancellationTokenSource();
        var provider = CreateProvider(processRunner, applicationStoppingSource.Token);

        var versionTask = provider.TryGetVersionAsync(
            workspace.Path,
            TestContext.Current.CancellationToken);
        await processRunner.RunStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        applicationStoppingSource.Cancel();

        Assert.Null(await versionTask);
        Assert.Equal(1, Assert.Single(processRunner.Disposables).DisposeCallCount);
    }

    private static DotnetSdkVersionProvider CreateProvider(
        IProcessRunner processRunner,
        CancellationToken applicationStopping = default) =>
        new(
            processRunner,
            NullLogger<DotnetSdkVersionProvider>.Instance,
            applicationStopping);
}
