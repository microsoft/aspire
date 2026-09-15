// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationHostLauncherTests
{
    [Fact]
    public async Task StartAsync_BootstrapSkipsUnavailableIntegrationHosts()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOST_BOOTSTRAP"] = "true",
            ["IntegrationHosts:0:PackageName"] = "needs-generated-sdk"
        }).Build();
        var launcher = new IntegrationHostLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance),
            configuration,
            NullLogger<IntegrationHostLauncher>.Instance);

        await launcher.StartAsync(TestContext.Current.CancellationToken);
        await launcher.ReadyAsync(TestContext.Current.CancellationToken);
        await launcher.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_InvalidIntegrationFaultsReadiness()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IntegrationHosts:0:PackageName"] = "missing-language-and-entrypoint"
        }).Build();
        var launcher = new IntegrationHostLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance),
            configuration,
            NullLogger<IntegrationHostLauncher>.Instance);

        var startupException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.StartAsync(TestContext.Current.CancellationToken));
        var readinessException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.ReadyAsync(TestContext.Current.CancellationToken));

        Assert.Same(startupException, readinessException);
    }

    [Fact]
    public async Task InitializeHostsAsync_WaitsForEveryRegistrationBeforeDiscovery()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var launcher = new IntegrationHostLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            registry, new ConfigurationBuilder().Build(), NullLogger<IntegrationHostLauncher>.Instance);
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstConnection = new IntegrationHostTestConnection(_ =>
        {
            discoveryStarted.TrySetResult();
            return Task.FromResult(JsonSerializer.SerializeToElement(new[] { new { id = "test/first" } }));
        });
        using var secondConnection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(new[] { new { id = "test/second" } }));
        registry.AddIntegrationHost(firstConnection.ServerRpc);

        var initialization = launcher.InitializeHostsAsync(
            2, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(initialization.IsCompleted);
        Assert.False(discoveryStarted.Task.IsCompleted);
        registry.AddIntegrationHost(secondConnection.ServerRpc);
        await initialization.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(registry.IsRegistered("test/first"));
        Assert.True(registry.IsRegistered("test/second"));
    }

    [Fact]
    public async Task InitializeHostsAsync_MissingRegistrationStopsDiscovery()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var launcher = new IntegrationHostLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            registry, new ConfigurationBuilder().Build(), NullLogger<IntegrationHostLauncher>.Instance);
        using var connection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(new[] { new { id = "test/partial" } }));
        registry.AddIntegrationHost(connection.ServerRpc);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            launcher.InitializeHostsAsync(2, TimeSpan.Zero, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.StartsWith("Only 1 of 2 integration hosts registered", exception.Message);
        Assert.False(registry.IsRegistered("test/partial"));
    }

    [Fact]
    public async Task InitializeHostsAsync_CancellationStopsWaitingForRegistration()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var launcher = new IntegrationHostLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<IntegrationHostLauncher>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var initialization = launcher.InitializeHostsAsync(
            1, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(10), cancellation.Token);
        Assert.False(initialization.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
    }

    [Fact]
    public void LogProcessExit_AfterShutdownDisposesProcess_DoesNotThrow()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new LanguageSupportResolver(
            services, () => [], NullLogger<LanguageSupportResolver>.Instance);
        var logger = new RecordingLogger<IntegrationHostLauncher>();
        var launcher = new IntegrationHostLauncher(
            resolver,
            new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance),
            new ConfigurationBuilder().Build(),
            logger);
        using var process = new Process();
        process.Dispose();

        launcher.LogProcessExit(process, 42, "test-integration", "host.mts");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Exit observer for integration host 'test-integration' (PID 42) stopped.", entry.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public void CreateProcessStartInfo_PreservesArgumentBoundariesAndExpandsEntryPoint()
    {
        var entryPoint = Path.GetFullPath(Path.Combine("integration packages", "host entry.mts"));
        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo(
            "integration-runtime",
            [
                "--no-install",
                "tsx",
                "{entryPoint}",
                "--entry={entryPoint}",
                "{entryPoint};{entryPoint}",
                "--label=a \"quoted\" value",
                @"C:\tools with spaces\trailing\",
                "\\\"",
                "",
                "{otherPlaceholder}"
            ],
            entryPoint,
            isWindows: false);

        Assert.Equal(
            [
                "--no-install",
                "tsx",
                entryPoint,
                $"--entry={entryPoint}",
                $"{entryPoint};{entryPoint}",
                "--label=a \"quoted\" value",
                @"C:\tools with spaces\trailing\",
                "\\\"",
                "",
                "{otherPlaceholder}"
            ],
            startInfo.ArgumentList.ToArray());
        Assert.Empty(startInfo.Arguments);
        Assert.Equal("integration-runtime", startInfo.FileName);
        Assert.Equal(Path.GetDirectoryName(entryPoint), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void CreateProcessStartInfo_AllowsEmptyArgumentList()
    {
        var entryPoint = Path.GetFullPath("host.mts");

        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo("integration-runtime", [], entryPoint, isWindows: false);

        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Theory]
    [InlineData("npx.cmd")]
    [InlineData("npx.CMD")]
    [InlineData("npx.bat")]
    public void CreateProcessStartInfo_WindowsBatchShim_UsesOuterQuotedCommand(string shim)
    {
        var command = $@"C:\Program Files\nodejs\{shim}";
        var entryPoint = Path.GetFullPath(Path.Combine("integration packages", "host entry.mts"));

        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo(
            command, ["--no-install", "tsx", "{entryPoint}"], entryPoint, isWindows: true);

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal($"/c \"\"{command}\" \"--no-install\" \"tsx\" \"{entryPoint}\"\"", startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }
}
