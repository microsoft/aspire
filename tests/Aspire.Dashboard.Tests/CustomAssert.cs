// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Dashboard.Tests;

public static class CustomAssert
{
    public static void AssertExceedsMinInterval(TimeSpan duration, TimeSpan minInterval)
    {
        // Timers are not precise, especially under CPU contention, so we allow for a margin of error.
        // The margin accounts for system scheduling delays and Task.Delay inaccuracy under load.
        var tolerance = TimeSpan.FromMilliseconds(200);
        var effectiveMinInterval = minInterval.Subtract(tolerance);
        Assert.True(
            duration >= effectiveMinInterval,
            $"Elapsed time {duration} should be greater than or equal to effective min interval {effectiveMinInterval} (min interval {minInterval} with tolerance {tolerance}).");
    }
}
