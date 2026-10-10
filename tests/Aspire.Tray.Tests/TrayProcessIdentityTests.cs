// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray.Tests;

public class TrayProcessIdentityTests
{
    [Fact]
    public async Task AnExactLiveLifetimeWaitsUntilCancelled()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new TrayProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var wait = identity.WaitForExitAsync(cancellation.Token);
        Assert.False(wait.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task AReusedPidCompletesWithoutWaitingForTheNewLifetime()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new TrayProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks - 1);

        await identity.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(process.HasExited);
    }
}
