// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

[RequiresTools(["node", "dotnet"])]
public class IntegrationHostProcessTests(IntegrationHostProcessFixture fixture, ITestOutputHelper output) : IClassFixture<IntegrationHostProcessFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupDeadline_FailsWithPhaseAndCleanupDiagnostics(bool discovery)
    {
        await using var server = await fixture.StartAsync(output, skipRegistration: !discovery, options: new()
        {
            RegistrationTimeout = TimeSpan.FromSeconds(5),
            DiscoveryTimeout = TimeSpan.FromSeconds(5),
            StallDiscovery = discovery
        });
        await server.WaitForFileAsync(discovery ? "discovery-started" : "host-pids");
        await server.Exit.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await server.DisposeAsync();

        Assert.NotEqual(0, server.ExitCode);
        Assert.Contains("lifetime-test", server.Diagnostics);
        Assert.Contains("registration started: attempt", server.Diagnostics);
        Assert.Contains("supervisor PID", server.Diagnostics);
        Assert.Contains("00:00:05", server.Diagnostics);
        Assert.Contains(
            discovery ? "Timed out getting capabilities from integration host 'lifetime-test'" : "System.TimeoutException",
            server.Diagnostics);
        Assert.Contains("Failed to initialize integration hosts at server startup.", server.Diagnostics);
        foreach (var pid in server.HostProcessIds)
        {
            await IntegrationHostServerProcess.AssertExitedAsync(pid);
        }
    }

    [Fact]
    public async Task ChangedSignature_StopsRecoveryWithActionableDiagnostics()
    {
        await using var server = await fixture.StartAsync(output, options: new() { MaxRestartAttempts = 1 });
        await server.ReadyAsync();
        Assert.True((await server.InvokeAsync("changeSignature")).TryGetProperty("$error", out _));
        await server.Exit.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await server.DisposeAsync();

        Assert.NotEqual(0, server.ExitCode);
        Assert.Contains("changed its capability signatures", server.Diagnostics);
        Assert.Contains("Restart the AppHost session and regenerate its SDK", server.Diagnostics);
        Assert.Contains("failed after 1 restart attempts", server.Diagnostics);
        Assert.Equal(2, server.HostProcessIds.Length);
        foreach (var pid in server.HostProcessIds)
        {
            await IntegrationHostServerProcess.AssertExitedAsync(pid);
        }
    }

    [Theory]
    [InlineData("stall")]
    [InlineData("block")]
    public async Task StalledInvocation_ReportsTimeoutReapsWorkersAndRecoversWithoutReplay(string action)
    {
        await using var server = await fixture.StartAsync(output, invocationTimeout: TimeSpan.FromSeconds(5));
        await server.ReadyAsync();
        var host = Assert.Single(server.HostProcessIds);
        var worker = int.Parse((await server.InvokeAsync("worker")).GetString()!);

        var failedCall = await server.InvokeAsync(action).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var error = failedCall.GetProperty("$error").GetProperty("message").GetString()!;
        Assert.StartsWith("Integration capability 'test.external/value' (invocation ", error);
        Assert.Contains("timed out after 00:00:05", error);
        await server.WaitForRecoveryAsync("generation-2");

        await IntegrationHostServerProcess.AssertExitedAsync(host);
        await IntegrationHostServerProcess.AssertExitedAsync(worker);
        Assert.Equal("once\n", await File.ReadAllTextAsync(Path.Combine(server.Directory, "side-effects")));
        Assert.Equal(2, server.HostProcessIds.Length);
        await server.DisposeAsync();
        Assert.Contains("stalled: capability test.external/value", server.Diagnostics);
        Assert.Contains("In-flight work is not replayed", server.Diagnostics);
        Assert.Contains("recovered after restart attempt 1; capabilities rediscovered.", server.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedExit_RestartsAndRediscoversWithoutReplayingSideEffects(bool useAppHostExecutable)
    {
        await using var server = await fixture.StartAsync(output, useAppHostExecutable: useAppHostExecutable);
        await server.ReadyAsync();
        Assert.Equal("generation-1", (await server.InvokeAsync()).GetString());
        var original = Assert.Single(server.HostProcessIds);
        var worker = int.Parse((await server.InvokeAsync("worker")).GetString()!);

        var failedCall = await server.InvokeAsync("crash");
        Assert.True(failedCall.TryGetProperty("$error", out _));
        await server.WaitForRecoveryAsync("generation-2");

        await IntegrationHostServerProcess.AssertExitedAsync(original);
        await IntegrationHostServerProcess.AssertExitedAsync(worker);
        Assert.Equal("once\n", await File.ReadAllTextAsync(Path.Combine(server.Directory, "side-effects")));
        Assert.Equal(2, server.HostProcessIds.Length);
        await server.DisposeAsync();
        Assert.Contains("recovered after restart attempt 1; capabilities rediscovered.", server.Diagnostics);
    }

    [Fact]
    public async Task CrashLoop_StopsTheSessionAfterThreeRestartAttempts()
    {
        await using var server = await fixture.StartAsync(output);
        await server.ReadyAsync();

        await server.InvokeAsync("crashLoop");
        await server.Exit.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await server.DisposeAsync();

        Assert.NotEqual(0, server.ExitCode);
        Assert.Contains("failed after 3 restart attempts. Stopping the AppHost session.", server.Diagnostics);
        Assert.Contains("Integration host recovery failed; the AppHost session was stopped.", server.Diagnostics);
        Assert.Equal("4", await File.ReadAllTextAsync(Path.Combine(server.Directory, "generation")));
        foreach (var processId in server.HostProcessIds)
        {
            await IntegrationHostServerProcess.AssertExitedAsync(processId);
        }
    }

    [Fact]
    public async Task NormalShutdown_ReapsHostsAndTheirWorkers()
    {
        await using var server = await fixture.StartAsync(output);
        await server.ReadyAsync();
        var host = Assert.Single(server.HostProcessIds);
        var worker = int.Parse((await server.InvokeAsync("worker")).GetString()!);

        await server.DisposeAsync();

        Assert.Equal(0, server.ExitCode);
        await IntegrationHostServerProcess.AssertExitedAsync(host);
        await IntegrationHostServerProcess.AssertExitedAsync(worker);
    }

    [Fact]
    public async Task SupervisorCrash_ReapsBlockedRuntimeAndWorkersBeforeRestarting()
    {
        await using var server = await fixture.StartAsync(output);
        await server.ReadyAsync();
        var host = Assert.Single(server.HostProcessIds);
        var worker = int.Parse((await server.InvokeAsync("worker")).GetString()!);
        var supervisor = int.Parse((await server.InvokeAsync("supervisor")).GetString()!);
        var blockedCall = server.InvokeAsync("block");
        await server.WaitForFileAsync("host-blocked");

        using (var process = Process.GetProcessById(supervisor))
        {
            process.Kill(entireProcessTree: false);
        }
        await server.WaitForRecoveryAsync("generation-2");

        await IntegrationHostServerProcess.AssertExitedAsync(host);
        await IntegrationHostServerProcess.AssertExitedAsync(worker);
        Assert.True((await blockedCall).TryGetProperty("$error", out _));
    }

    [Fact]
    public async Task Disconnect_ReapsTheLiveRuntimeBeforeRediscovery()
    {
        await using var server = await fixture.StartAsync(output);
        await server.ReadyAsync();
        var host = Assert.Single(server.HostProcessIds);

        Assert.True((await server.InvokeAsync("disconnect")).TryGetProperty("$error", out _));
        await server.WaitForRecoveryAsync("generation-2");

        await IntegrationHostServerProcess.AssertExitedAsync(host);
        Assert.Equal(2, server.HostProcessIds.Length);
    }

    [Fact]
    public async Task ShutdownDuringRegistration_ReapsTheUnregisteredHost()
    {
        await using var server = await fixture.StartAsync(output, skipRegistration: true);
        await server.WaitForFileAsync("host-pids");
        var host = Assert.Single(server.HostProcessIds);

        await server.DisposeAsync();

        await IntegrationHostServerProcess.AssertExitedAsync(host);
    }

    [Fact]
    public async Task ServerCrash_ReapsBlockedHostAndItsWorker()
    {
        await using var server = await fixture.StartAsync(output);
        await server.ReadyAsync();
        var host = Assert.Single(server.HostProcessIds);
        var worker = int.Parse((await server.InvokeAsync("worker")).GetString()!);
        var blockedCall = server.InvokeAsync("block");
        await server.WaitForFileAsync("host-blocked");

        server.KillServer();
        await server.Exit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await IntegrationHostServerProcess.AssertExitedAsync(host);
        await IntegrationHostServerProcess.AssertExitedAsync(worker);
        await Assert.ThrowsAnyAsync<Exception>(() => blockedCall);
    }

    [Fact]
    public async Task MissingCommand_FailsPromptlyWithContextualInstallationDiagnostics()
    {
        await using var server = await fixture.StartAsync(output, missingCommand: true);
        await server.Exit.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await server.DisposeAsync();

        Assert.NotEqual(0, server.ExitCode);
        Assert.Contains("Cannot launch integration host 'lifetime-test' [test/node]", server.Diagnostics);
        Assert.Contains("Command 'aspire-missing-integration-runtime' not found. Please ensure it is installed and in your PATH.", server.Diagnostics);
        Assert.False(File.Exists(Path.Combine(server.Directory, "host-pids")));
    }

    [Fact]
    public async Task StartupExit_FailsPromptlyAndPreservesRuntimeDiagnostics()
    {
        await using var server = await fixture.StartAsync(output, failStartup: true);

        await server.Exit.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await server.DisposeAsync();

        Assert.Contains("original-runtime-startup-error", server.Diagnostics);
        Assert.Contains("exited with code 23", server.Diagnostics);
    }
}
