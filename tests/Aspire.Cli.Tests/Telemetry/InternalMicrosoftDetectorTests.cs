// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

public sealed class InternalMicrosoftDetectorTests
{
    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_UsesFreshCache()
    {
        using var tempDirectory = new TestTempDirectory();
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(tempDirectory.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "isInternalMicrosoft": true,
              "source": "cached source",
              "alias": "cached.alias",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var probeRan = false;
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [
                [
                    new InternalMicrosoftProbe("should not run", _ =>
                    {
                        probeRan = true;
                        return Task.FromResult(InternalMicrosoftProbeResult.NotDetected);
                    })
                ]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("cached source", result.Source);
        Assert.Equal("cached.alias", result.Alias);
        Assert.False(probeRan);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsProbesWhenCacheIsStaleAndUpdatesCache()
    {
        using var tempDirectory = new TestTempDirectory();
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(tempDirectory.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "isInternalMicrosoft": false,
              "lastRunUtc": "2026-06-16T05:59:59+00:00"
            }
            """);
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [
                [new InternalMicrosoftProbe("positive", _ => Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "stale.alias")))]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("stale.alias", result.Alias);

        var updatedCache = await File.ReadAllTextAsync(cacheFilePath);
        Assert.Contains("\"isInternalMicrosoft\": true", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"positive\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"alias\": \"stale.alias\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"lastRunUtc\": \"2026-06-16T12:00:00+00:00\"", updatedCache, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsNextStageOnlyWhenPreviousStageDoesNotDetect()
    {
        using var tempDirectory = new TestTempDirectory();
        var calls = new List<string>();
        var detector = CreateDetector(
            Path.Combine(tempDirectory.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [new InternalMicrosoftProbe("stage 1", _ =>
                {
                    calls.Add("stage 1");
                    return Task.FromResult(InternalMicrosoftProbeResult.NotDetected);
                })],
                [new InternalMicrosoftProbe("stage 2", _ =>
                {
                    calls.Add("stage 2");
                    return Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "stage.alias"));
                })],
                [new InternalMicrosoftProbe("stage 3", _ =>
                {
                    calls.Add("stage 3");
                    return Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "unused.alias"));
                })]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("stage 2", result.Source);
        Assert.Equal("stage.alias", result.Alias);
        Assert.Equal(["stage 1", "stage 2"], calls);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_CancelsOtherProbesInStageAfterSuccessfulProbe()
    {
        using var tempDirectory = new TestTempDirectory();
        var slowProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowProbeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(tempDirectory.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("positive", async _ =>
                    {
                        await slowProbeStarted.Task;
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "positive.alias");
                    }),
                    new InternalMicrosoftProbe("slow", async cancellationToken =>
                    {
                        slowProbeStarted.SetResult();

                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            slowProbeCancelled.SetResult();
                            throw;
                        }

                        return InternalMicrosoftProbeResult.NotDetected;
                    })
                ]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("positive.alias", result.Alias);
        await slowProbeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReturnsPositiveResultWhenCancelledProbeFaultsDuringDrain()
    {
        using var tempDirectory = new TestTempDirectory();
        var faultingProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(tempDirectory.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("positive", async _ =>
                    {
                        await faultingProbeStarted.Task;
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "fault.alias");
                    }),
                    new InternalMicrosoftProbe("faulting", async cancellationToken =>
                    {
                        faultingProbeStarted.SetResult();

                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw new NotSupportedException("Unexpected probe failure after cancellation.");
                        }

                        return InternalMicrosoftProbeResult.NotDetected;
                    })
                ]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("fault.alias", result.Alias);
    }

    private static InternalMicrosoftDetector CreateDetector(string cacheFilePath, DateTimeOffset now, IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> probeStages)
    {
        return new InternalMicrosoftDetector(cacheFilePath, new FixedTimeProvider(now), NullLogger<InternalMicrosoftDetector>.Instance, probeStages);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
