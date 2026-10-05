// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class ProcessSignalerTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void TryGetRunningProcess_CurrentProcess_WithStableStartTime_ReturnsProcess()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddXunit(testOutputHelper));
        var logger = loggerFactory.CreateLogger<ProcessSignalerTests>();
        var currentProcess = Process.GetCurrentProcess();

        // The stable path receives the full millisecond-precision identity time (the AppHost's
        // StableStartedAt, or a locally captured TryGetProcessStartTime value), so an exact reading
        // of the same process must match.
        var stableStartTime = ProcessStartTimeHelper.GetCurrentProcessStartTime();

        using var result = ProcessSignaler.TryGetRunningProcess(
            currentProcess.Id, stableStartTime, logger);

        Assert.NotNull(result);
        Assert.Equal(currentProcess.Id, result.Id);
    }

    [Fact]
    public void TryGetRunningProcess_CurrentProcess_WithStartTimeBeyondTolerance_ReturnsNull()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddXunit(testOutputHelper));
        var logger = loggerFactory.CreateLogger<ProcessSignalerTests>();
        var currentProcess = Process.GetCurrentProcess();

        // One second off is still the same (or an immediately adjacent) wall-clock second, but far beyond
        // the millisecond reuse tolerance. This models a PID recycled within the same second: it must not
        // match now that the stable path compares identity at millisecond precision rather than truncating
        // to whole seconds.
        var mismatchedStartTime = ProcessStartTimeHelper.GetCurrentProcessStartTime().AddSeconds(1);

        using var result = ProcessSignaler.TryGetRunningProcess(
            currentProcess.Id, mismatchedStartTime, logger);

        Assert.Null(result);
    }
}
