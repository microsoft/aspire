// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Xunit;

namespace Aspire.Managed.Tests;

public class ParentProcessWatchdogTests
{
    [Fact]
    public void GetExpectedParentStartTimeUnix_PrefersStableValue()
    {
        var environment = new Dictionary<string, string?>
        {
            [KnownConfigNames.CliProcessStarted] = "1000",
            [KnownConfigNames.CliProcessStartedStable] = "1001"
        };

        var startTime = ParentProcessWatchdog.GetExpectedParentStartTimeUnix(environment.GetValueOrDefault, out var useRuntimeStartTime);

        Assert.Equal(1001, startTime);
        Assert.False(useRuntimeStartTime);
    }

    [Fact]
    public void GetExpectedParentStartTimeUnix_UsesRuntimeComparisonForLegacyValue()
    {
        var environment = new Dictionary<string, string?>
        {
            [KnownConfigNames.CliProcessStarted] = "1000"
        };

        var startTime = ParentProcessWatchdog.GetExpectedParentStartTimeUnix(environment.GetValueOrDefault, out var useRuntimeStartTime);

        Assert.Equal(1000, startTime);
        Assert.True(useRuntimeStartTime);
    }

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
