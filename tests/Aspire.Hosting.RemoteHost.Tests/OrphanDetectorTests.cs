// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class OrphanDetectorTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompletesWithoutStoppingWhenNoParentPidConfigured()
    {
        var configuration = new ConfigurationBuilder().Build();
        var stopCalled = false;
        var lifetime = new HostLifetimeStub(() => stopCalled = true);

        var detector = new OrphanDetector(configuration, lifetime, NullLogger<OrphanDetector>.Instance);

        await detector.StartAsync(CancellationToken.None).WaitAsync(s_timeout);
        await detector.ExecuteTask!.WaitAsync(s_timeout);

        Assert.False(stopCalled);
    }

    [Fact]
    public async Task StopsWhenParentPidNotRunning_PidOnly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["REMOTE_APP_HOST_PID"] = "1111" })
            .Build();

        var stopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopTcs.TrySetResult());

        var detector = new OrphanDetector(configuration, lifetime, NullLogger<OrphanDetector>.Instance)
        {
            IsProcessRunning = _ => false,
            // Ensure the start-time path is not used when no start time is configured.
            IsProcessRunningWithStartTime = (_, _) => throw new InvalidOperationException("Start-time path should not be used without REMOTE_APP_HOST_STARTED."),
        };

        await detector.StartAsync(CancellationToken.None).WaitAsync(s_timeout);
        await stopTcs.Task.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task StopsWhenParentPidNotRunning_WithStartTimeVerification()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_PID"] = "1111",
                ["REMOTE_APP_HOST_STARTED"] = "1700000000",
            })
            .Build();

        var stopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopTcs.TrySetResult());

        var detector = new OrphanDetector(configuration, lifetime, NullLogger<OrphanDetector>.Instance)
        {
            // PID-only path must not run when a start time is configured.
            IsProcessRunning = _ => throw new InvalidOperationException("PID-only path should not be used when REMOTE_APP_HOST_STARTED is set."),
            IsProcessRunningWithStartTime = (_, _) => false,
        };

        await detector.StartAsync(CancellationToken.None).WaitAsync(s_timeout);
        await stopTcs.Task.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task StopsWhenStartTimeMismatch_PidReuse()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_PID"] = "1111",
                ["REMOTE_APP_HOST_STARTED"] = "1700000000",
            })
            .Build();

        var stopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopTcs.TrySetResult());

        // The PID exists but its start time does not match — a recycled PID. The detector must treat the
        // parent as gone rather than keeping the orphaned server alive.
        var detector = new OrphanDetector(configuration, lifetime, NullLogger<OrphanDetector>.Instance)
        {
            IsProcessRunningWithStartTime = (_, _) => false,
        };

        await detector.StartAsync(CancellationToken.None).WaitAsync(s_timeout);
        await stopTcs.Task.WaitAsync(s_timeout);
    }

    [Fact]
    public async Task FallsBackToAspireCliStartedWhenRemoteStartedMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_PID"] = "1111",
                ["ASPIRE_CLI_STARTED"] = "1700000000",
            })
            .Build();

        var stopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopTcs.TrySetResult());

        var detector = new OrphanDetector(configuration, lifetime, NullLogger<OrphanDetector>.Instance)
        {
            IsProcessRunning = _ => throw new InvalidOperationException("Start time from ASPIRE_CLI_STARTED should select the start-time path."),
            IsProcessRunningWithStartTime = (_, _) => false,
        };

        await detector.StartAsync(CancellationToken.None).WaitAsync(s_timeout);
        await stopTcs.Task.WaitAsync(s_timeout);
    }

    private sealed class HostLifetimeStub(Action stopImplementation) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => throw new NotImplementedException();

        public CancellationToken ApplicationStopped => throw new NotImplementedException();

        public CancellationToken ApplicationStopping => throw new NotImplementedException();

        public void StopApplication() => stopImplementation();
    }
}
