// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

[RequiresTools(["node", "dotnet"])]
public class IntegrationHostProcessTests(IntegrationHostProcessFixture fixture) : IClassFixture<IntegrationHostProcessFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedExit_RestartsAndRediscoversWithoutReplayingSideEffects(bool useAppHostExecutable)
    {
        await using var server = await fixture.StartAsync(useAppHostExecutable: useAppHostExecutable);
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
        await using var server = await fixture.StartAsync();
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
        await using var server = await fixture.StartAsync();
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
        await using var server = await fixture.StartAsync();
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
        await using var server = await fixture.StartAsync();
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
        await using var server = await fixture.StartAsync(skipRegistration: true);
        await server.WaitForFileAsync("host-pids");
        var host = Assert.Single(server.HostProcessIds);

        await server.DisposeAsync();

        await IntegrationHostServerProcess.AssertExitedAsync(host);
    }

    [Fact]
    public async Task ServerCrash_ReapsBlockedHostAndItsWorker()
    {
        await using var server = await fixture.StartAsync();
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
    public async Task StartupExit_FailsPromptlyAndPreservesRuntimeDiagnostics()
    {
        await using var server = await fixture.StartAsync(failStartup: true);

        await server.Exit.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Contains("original-runtime-startup-error", server.Diagnostics);
        Assert.Contains("exited with code 23", server.Diagnostics);
    }
}
