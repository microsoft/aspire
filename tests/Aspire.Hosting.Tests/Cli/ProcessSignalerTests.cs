// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class ProcessSignalerTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(0, true)]       // Exact match
    [InlineData(0.8, true)]     // Sub-second difference within the same second after truncation
    [InlineData(1.2, false)]    // Next second: a recycled PID must not match
    [InlineData(1, false)]      // Exactly one second later is a different whole second
    [InlineData(3, false)]      // 3 seconds apart (PID reuse)
    [InlineData(-0.5, false)]   // Previous second after truncation
    public void AreClose_ComparesAtSecondGranularity(double offsetSeconds, bool expected)
    {
        var baseTime = new DateTime(2025, 6, 12, 10, 30, 45, 0, DateTimeKind.Local);
        var expectedStartTime = new DateTimeOffset(baseTime);
        var processStartTime = baseTime.AddSeconds(offsetSeconds);

        var result = ProcessSignaler.AreClose(expectedStartTime, processStartTime);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryGetRunningProcess_CurrentProcess_WithTruncatedStartTime_ReturnsProcess()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddXunit(testOutputHelper));
        var logger = loggerFactory.CreateLogger<ProcessSignalerTests>();
        var currentProcess = Process.GetCurrentProcess();

        // Simulate what the CLI does: truncate to unix seconds
        var truncatedStartTime = DateTimeOffset.FromUnixTimeSeconds(
            ((DateTimeOffset)currentProcess.StartTime).ToUnixTimeSeconds());

        using var result = ProcessSignaler.TryGetRunningProcess(
            currentProcess.Id, truncatedStartTime, logger);

        Assert.NotNull(result);
        Assert.Equal(currentProcess.Id, result.Id);
    }
}

