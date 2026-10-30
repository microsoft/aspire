// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Rpc;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeApplicationLifetimeTests
{
    [Fact]
    public async Task StopWithoutCooperativeGuestCompletesImmediately()
    {
        using var lifetime = new NativeApplicationLifetime();
        await lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), TimeProvider.System,
            () => Assert.Fail("No guest deadline should run."), CancellationToken.None);
        Assert.Throws<InvalidOperationException>(lifetime.CreateRun);
    }

    [Fact]
    public async Task CooperativeGuestReceivesStopBeforeTransportShutdownAndHasBoundedCleanup()
    {
        var clock = new FakeTimeProvider();
        using var lifetime = new NativeApplicationLifetime();
        var run = lifetime.CreateRun();
        var timedOut = false;
        var stopping = lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), clock, () => timedOut = true, CancellationToken.None);
        await run.WaitForStop().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stopping.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(timedOut);
        run.Close();
    }

    [Fact]
    public async Task ConnectionOwnedLifetimeDrainsAfterGuestCleanup()
    {
        using var lifetime = new NativeApplicationLifetime();
        using var handles = new NativeHandles(4);
        var run = lifetime.CreateRun();
        handles.Add(run, "run");
        var stopping = lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), TimeProvider.System,
            () => Assert.Fail("Guest already finished."), CancellationToken.None);
        await run.WaitForStop();
        Assert.False(stopping.IsCompleted);
        handles.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        run.Close();
        Assert.True(run.IsRevoked);
    }

    [Fact]
    public async Task DisposalCancelsPendingWaitersAndIsIdempotent()
    {
        var lifetime = new NativeApplicationLifetime();
        var requested = lifetime.CreateRun().WaitForStop();
        lifetime.Dispose();
        lifetime.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requested);
        Assert.Throws<ObjectDisposedException>(lifetime.CreateRun);
        await lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), TimeProvider.System,
            () => Assert.Fail("Disposed lifetime must not report a deadline."), CancellationToken.None);
    }

    [Fact]
    public async Task DisconnectedGuestDoesNotDelaySubsequentShutdown()
    {
        using var lifetime = new NativeApplicationLifetime();
        var run = lifetime.CreateRun();
        var pending = run.WaitForStop();
        run.Close();
        run.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), TimeProvider.System,
            () => Assert.Fail("Disconnected guest must not delay shutdown."), CancellationToken.None);
    }

    [Fact]
    public async Task RejectedRunHandleDoesNotDelayShutdown()
    {
        using var lifetime = new NativeApplicationLifetime();
        using var handles = new NativeHandles(0);
        var run = lifetime.CreateRun();
        Assert.Throws<RpcFault>(() => handles.Add(run, "run"));
        Assert.True(run.IsRevoked);
        await lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), TimeProvider.System,
            () => Assert.Fail("Rejected handle must not retain guest ownership."), CancellationToken.None);
    }

    [Fact]
    public async Task AllRegisteredGuestsMustReleaseBeforeShutdownCompletes()
    {
        using var lifetime = new NativeApplicationLifetime();
        var first = lifetime.CreateRun();
        var second = lifetime.CreateRun();
        var stopping = lifetime.RequestStopAsync(TimeSpan.FromSeconds(5), TimeProvider.System,
            () => Assert.Fail("Guests already finished."), CancellationToken.None);
        first.Close();
        Assert.False(stopping.IsCompleted);
        second.Close();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
