// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Managed.Tests;

public class ParentProcessWatchdogTests
{
    [Fact]
    public async Task ParentExitedCallback_StillForceExitsWhenCancellationCallbackThrows()
    {
        using var operationCts = new CancellationTokenSource();
        using var registration = operationCts.Token.Register(static () => throw new InvalidOperationException("simulated cancellation callback failure"));
        int? exitCode = null;

        await ParentProcessWatchdog.OnParentExitedAsync(
            operationCts,
            CancellationToken.None,
            forceExitGracePeriod: TimeSpan.Zero,
            exit: code => exitCode = code);

        Assert.True(operationCts.IsCancellationRequested);
        Assert.Equal(124, exitCode);
    }
}
