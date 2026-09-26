// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Postgres;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.PostgreSQL.Tests;

public class PostgresServerHealthCheckTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NullOrEmptyConnectionString_ReturnsUnhealthy_WithoutProbing(string? connectionString)
    {
        var probeInvoked = false;
        var check = new PostgresServerHealthCheck(() => connectionString, (count, ct) =>
        {
            probeInvoked = true;
            return Task.CompletedTask;
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.False(probeInvoked);
    }

    [Fact]
    public async Task WhileNotStable_RunsTheFullConsecutiveWindow()
    {
        var observedCount = 0;
        var check = new PostgresServerHealthCheck(() => "Host=localhost", (count, ct) =>
        {
            observedCount = count;
            return Task.CompletedTask;
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        // Before latching the gate requires more than a single probe (the stability window).
        Assert.True(observedCount > 1, $"Expected a multi-probe window but got {observedCount}.");
    }

    [Fact]
    public async Task ProbeFailureWhileNotStable_ReturnsUnhealthy_AndDoesNotLatch()
    {
        var counts = new List<int>();
        var shouldThrow = true;
        var check = new PostgresServerHealthCheck(() => "Host=localhost", (count, ct) =>
        {
            counts.Add(count);
            return shouldThrow ? Task.FromException(new InvalidOperationException("reset")) : Task.CompletedTask;
        });

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, first.Status);

        // Still not latched: the next poll runs the full window again, not a single probe.
        shouldThrow = false;
        var second = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, second.Status);

        Assert.Equal(2, counts.Count);
        Assert.True(counts[0] > 1);
        Assert.True(counts[1] > 1);
    }

    [Fact]
    public async Task AfterAllProbesSucceed_LatchesToSingleProbe()
    {
        var counts = new List<int>();
        var check = new PostgresServerHealthCheck(() => "Host=localhost", (count, ct) =>
        {
            counts.Add(count);
            return Task.CompletedTask;
        });

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, first.Status);
        Assert.Equal(HealthStatus.Healthy, second.Status);

        Assert.Equal(2, counts.Count);
        Assert.True(counts[0] > 1, "First check should run the full stability window.");
        Assert.Equal(1, counts[1]); // Latched: a single cheap probe thereafter.
    }

    [Fact]
    public async Task AfterLatch_ProbeFailure_ReturnsUnhealthy_AndResetsLatch()
    {
        var fail = false;
        var counts = new List<int>();
        var check = new PostgresServerHealthCheck(() => "Host=localhost", (count, ct) =>
        {
            counts.Add(count);
            return fail ? Task.FromException(new InvalidOperationException("down")) : Task.CompletedTask;
        });

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        fail = true;
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        fail = false;
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        Assert.True(counts[0] > 1);
        Assert.Equal(1, counts[1]);
        Assert.True(counts[2] > 1);
    }

    [Fact]
    public async Task ConcurrentSingleProbeSuccess_DoesNotRestoreLatchAfterFailure()
    {
        var singleProbeCount = 0;
        var staleProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new ConcurrentQueue<int>();
        var check = new PostgresServerHealthCheck(() => "Host=localhost", async (count, ct) =>
        {
            counts.Enqueue(count);
            if (count > 1)
            {
                return;
            }

            if (Interlocked.Increment(ref singleProbeCount) == 1)
            {
                staleProbeStarted.SetResult();
                await releaseStaleProbe.Task.WaitAsync(ct);
                return;
            }

            throw new InvalidOperationException("down");
        });

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        var staleSuccess = check.CheckHealthAsync(new HealthCheckContext());
        await staleProbeStarted.Task;
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        releaseStaleProbe.SetResult();
        Assert.Equal(HealthStatus.Healthy, (await staleSuccess).Status);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        Assert.True(counts.Last() > 1);
    }

    [Fact]
    public async Task CancellationDuringProbe_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var check = new PostgresServerHealthCheck(() => "Host=localhost", (count, ct) =>
            Task.FromException(new OperationCanceledException(ct)));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }
}
