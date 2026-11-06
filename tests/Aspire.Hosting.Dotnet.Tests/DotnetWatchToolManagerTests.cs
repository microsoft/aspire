// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchToolManagerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CompleteCacheHitUsesOnlyFilesAndNeverInvokesInstaller()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);

        var first = await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);
        installer.BeforeInstall = _ => throw new InvalidOperationException("No process or network operation is permitted on a warm cache hit.");
        var second = await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal("10.0.403", second.Version);
        Assert.Equal(10, second.RuntimeMajor);
        Assert.True(File.Exists(second.DllPath));
        var request = Assert.Single(installer.Requests);
        Assert.Equal(workspace.Path, request.WorkingDirectory);
    }

    [Fact]
    public async Task ExplicitPathTakesPrecedenceOverInvalidVersionWithoutCreatingCache()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var payloadDirectory = Path.Combine(workspace.Path, "custom");
        var expected = DotnetWatchToolTestPayload.Create(payloadDirectory);
        var manager = CreateManager(workspace.Path, installer);

        var installation = await manager.EnsureAsync(
            workspace.Path, "custom", "not a version", TestContext.Current.CancellationToken);

        Assert.Equal(expected, installation.DllPath);
        Assert.Equal("custom", installation.Version);
        Assert.Empty(installer.Requests);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "cache")));
    }

    [Theory]
    [InlineData("10.0.403")]
    [InlineData("11.0.100-rc.2.26504.105")]
    [InlineData("99.0.0-custom")]
    public async Task ExactVersionOverridesAreNotRestrictedToDefaultPin(string version)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);

        var installation = await manager.EnsureAsync(workspace.Path, null, version, TestContext.Current.CancellationToken);

        Assert.Equal(version, installation.Version);
        Assert.Equal(version, Assert.Single(installer.Requests).Version);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("[10.0.403]")]
    [InlineData("../other")]
    [InlineData("10.0.*")]
    [InlineData("10.0.403-rc..1")]
    [InlineData("10.0.403+metadata.")]
    [InlineData(" ")]
    public async Task RangesAndPathsCannotBecomeCacheVersions(string version)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);

        await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => manager.EnsureAsync(workspace.Path, null, version, TestContext.Current.CancellationToken));

        Assert.Empty(installer.Requests);
    }

    [Fact]
    public async Task ConcurrentEnsuresPublishOneCompleteInstallation()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new TestDotnetWatchToolInstaller
        {
            BeforeInstall = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        var manager = CreateManager(workspace.Path, installer);

        var first = manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(results[0], results[1]);
        Assert.Single(installer.Requests);
        Assert.Empty(Directory.GetDirectories(Path.Combine(workspace.Path, "cache"), ".install-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DifferentVersionsCoexistWithoutUpdatingEitherWarmInstallation()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);
        var baseline = await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);
        var alternate = await manager.EnsureAsync(workspace.Path, null, "99.0.0-custom", TestContext.Current.CancellationToken);
        installer.BeforeInstall = _ => throw new InvalidOperationException("Warm installations must not be updated.");

        Assert.NotEqual(baseline.DllPath, alternate.DllPath);
        Assert.Equal(baseline, await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(alternate, await manager.EnsureAsync(workspace.Path, null, "99.0.0-custom", TestContext.Current.CancellationToken));
        Assert.True(File.Exists(baseline.DllPath));
        Assert.True(File.Exists(alternate.DllPath));
        Assert.Equal(2, installer.Requests.Count);
    }

    [Fact]
    public async Task CanceledAcquisitionDoesNotPublishAndCanBeRetried()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new TestDotnetWatchToolInstaller
        {
            BeforeInstall = async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var manager = CreateManager(workspace.Path, installer);

        var pending = manager.EnsureAsync(workspace.Path, null, null, cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace.Path, "cache"), "installation.json", SearchOption.AllDirectories));
        installer.BeforeInstall = _ => Task.CompletedTask;

        var installation = await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(installation.DllPath));
        Assert.Equal(2, installer.Requests.Count);
    }

    [Fact]
    public async Task IncompletePublishedCacheFailsRatherThanReplacingFilesUsedByAnotherRun()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);
        var installation = await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);
        File.Delete(Path.ChangeExtension(installation.DllPath, ".deps.json"));

        var exception = await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("incomplete or incompatible", exception.Message);
        Assert.Single(installer.Requests);
        Assert.True(File.Exists(installation.DllPath));
    }

    [Fact]
    public async Task MissingExplicitPayloadNeverFallsBackToDefaultAcquisition()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);

        await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => manager.EnsureAsync(workspace.Path, "missing", null, TestContext.Current.CancellationToken));

        Assert.Empty(installer.Requests);
    }

    [Fact]
    public async Task WhitespacePathDoesNotSilentlySelectTheDefaultPackage()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();

        await Assert.ThrowsAsync<DotnetWatchToolException>(() => CreateManager(workspace.Path, installer).EnsureAsync(
            workspace.Path, " ", null, TestContext.Current.CancellationToken));

        Assert.Empty(installer.Requests);
    }

    [Theory]
    [InlineData("Files", "null")]
    [InlineData("Files", "[null]")]
    [InlineData("Files", """[{"Path":null,"Length":1}]""")]
    [InlineData("Files", """[{"Path":"../outside","Length":1}]""")]
    [InlineData("EntryPoint", "null")]
    [InlineData("EntryPoint", "\"../outside.dll\"")]
    [InlineData("EntryPoint", "\"\\u0000\"")]
    public async Task MalformedManifestProducesActionableFailureWithoutReinstalling(string property, string json)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var manager = CreateManager(workspace.Path, installer);
        await manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken);
        var manifestPath = Assert.Single(Directory.GetFiles(
            Path.Combine(workspace.Path, "cache"), "installation.json", SearchOption.AllDirectories));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken))!;
        manifest[property] = JsonNode.Parse(json);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => manager.EnsureAsync(workspace.Path, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("incomplete or incompatible", exception.Message);
        Assert.Single(installer.Requests);
    }

    [Fact]
    public async Task ExplicitPayloadMustRetainItsDeclaredDependencies()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var directory = Path.Combine(workspace.Path, "custom");
        DotnetWatchToolTestPayload.Create(directory);
        File.Delete(Path.Combine(directory, "Dependency.dll"));

        var exception = await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => CreateManager(workspace.Path, installer).EnsureAsync(
                workspace.Path, directory, null, TestContext.Current.CancellationToken));

        Assert.Contains("Dependency.dll", exception.Message);
        Assert.Empty(installer.Requests);
    }

    [Theory]
    [InlineData("hotreload/net6.0/Microsoft.Extensions.DotNetDeltaApplier.dll")]
    [InlineData("hotreload/net6.0/Microsoft.AspNetCore.Watch.BrowserRefresh.dll")]
    [InlineData("hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll")]
    public async Task ExplicitPayloadMustRetainItsAgents(string agent)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var directory = Path.Combine(workspace.Path, "custom");
        DotnetWatchToolTestPayload.Create(directory);
        File.Delete(Path.Combine(directory, agent));

        var exception = await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => CreateManager(workspace.Path, installer).EnsureAsync(
                workspace.Path, directory, null, TestContext.Current.CancellationToken));

        Assert.Contains("missing agent", exception.Message);
        Assert.Empty(installer.Requests);
    }

    [Fact]
    public async Task AgentLayoutDoesNotFollowTheToolsOwnRuntimeVersion()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var directory = Path.Combine(workspace.Path, "custom");
        var dll = DotnetWatchToolTestPayload.Create(directory);
        var configuration = await File.ReadAllTextAsync(Path.ChangeExtension(dll, ".runtimeconfig.json"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.ChangeExtension(dll, ".runtimeconfig.json"), configuration.Replace("10.0.14", "11.0.0"), TestContext.Current.CancellationToken);

        var installation = await CreateManager(workspace.Path, installer).EnsureAsync(
            workspace.Path, directory, null, TestContext.Current.CancellationToken);

        Assert.Equal(11, installation.RuntimeMajor);
        Assert.Empty(installer.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runtimeOptions":null}""")]
    [InlineData("""{"runtimeOptions":{"frameworks":[]}}""")]
    public async Task MalformedExplicitRuntimeConfigurationDoesNotEscapeAsAnUnclassifiedException(string configuration)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var installer = new TestDotnetWatchToolInstaller();
        var directory = Path.Combine(workspace.Path, "custom");
        var dll = DotnetWatchToolTestPayload.Create(directory);
        await File.WriteAllTextAsync(Path.ChangeExtension(dll, ".runtimeconfig.json"), configuration, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DotnetWatchToolException>(
            () => CreateManager(workspace.Path, installer).EnsureAsync(
                workspace.Path, directory, null, TestContext.Current.CancellationToken));

        Assert.Empty(installer.Requests);
    }

    private DotnetWatchToolManager CreateManager(string workspace, IDotnetWatchToolInstaller installer) =>
        new(Path.Combine(workspace, "cache"), "10.0.403", installer, output.WriteLine);
}
