// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Cli.DotNet;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

public sealed class InternalMicrosoftDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ProbeFailureMetadataValues_AreBoundedStaticIdentifiers()
    {
        foreach (var type in new[]
        {
            typeof(InternalMicrosoftProbeFailureCode),
            typeof(InternalMicrosoftProbeFailureStage),
            typeof(InternalMicrosoftProbeExceptionType)
        })
        {
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var value = Assert.IsType<string>(field.GetValue(null));
                Assert.InRange(value.Length, 1, 64);
                Assert.Matches("^[A-Za-z0-9._]+$", value);
            }
        }
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_UsesFreshCache()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "cached source",
              "alias": "cached.alias",
              "domain": "CACHED",
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
        Assert.Equal("CACHED", result.Domain);
        Assert.Equal(InternalMicrosoftDetectorOutcome.Detected, result.Outcome);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Hit, result.CacheStatus);
        Assert.False(probeRan);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_BypassesFreshNegativeCacheWhenVsCodeAliasAppears()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": false,
              "isCIEnvironment": false,
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var probeRan = false;
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("current VS Code account", _ =>
            {
                probeRan = true;
                return Task.FromResult(new InternalMicrosoftProbeResult(true, "current.alias", null));
            })]],
            vsCodeMicrosoftAlias: "current.alias");

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(probeRan);
        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_BypassesFreshBooleanOnlyCacheWhenVsCodeAliasAppears()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "gh CLI GitHub org membership",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("current VS Code account", _ =>
                Task.FromResult(new InternalMicrosoftProbeResult(true, "current.alias", null)))]],
            vsCodeMicrosoftAlias: "current.alias");

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_CachesWinningAliasSeparatelyFromObservedVsCodeAlias()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var firstDetector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("Windows identity", _ =>
                Task.FromResult(new InternalMicrosoftProbeResult(true, "windows.alias", "REDMOND")))]],
            vsCodeMicrosoftAlias: "vscode.alias");

        var firstResult = await firstDetector.IsInternalMicrosoftMachineAsync();
        Assert.Equal("windows.alias", firstResult.Alias);

        var probeRan = false;
        var secondDetector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("unexpected", _ =>
            {
                probeRan = true;
                return Task.FromResult(InternalMicrosoftProbeResult.NotDetected);
            })]],
            vsCodeMicrosoftAlias: "vscode.alias");

        var secondResult = await secondDetector.IsInternalMicrosoftMachineAsync();

        Assert.False(probeRan);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Hit, secondResult.CacheStatus);
        Assert.Equal("windows.alias", secondResult.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RejectsFreshVsCodeCacheAfterSignOut()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "VS Code Microsoft tenant",
              "alias": "old.alias",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [],
            vsCodeMicrosoftProviderAvailable: true);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_PreservesFreshVsCodeCacheWhenProviderIsUnavailable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "VS Code Microsoft tenant",
              "alias": "cached.alias",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(cacheFilePath, now, []);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Hit, result.CacheStatus);
        Assert.Equal("cached.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_PreservesFreshVsCodeCacheWhenProviderFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "VS Code Microsoft tenant",
              "alias": "cached.alias",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var provider = new TestVsCodeMicrosoftAccountProvider
        {
            GetInternalMicrosoftAccountAsyncCallback = _ => throw new InvalidOperationException("Simulated provider failure.")
        };
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [],
            vsCodeMicrosoftAccountProvider: provider);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Hit, result.CacheStatus);
        Assert.Equal("cached.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_BoundsVsCodeAccountQueryAndContinuesOtherProbes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuery = new TaskCompletionSource<VsCodeMicrosoftAccountState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestVsCodeMicrosoftAccountProvider
        {
            GetInternalMicrosoftAccountAsyncCallback = _ =>
            {
                queryStarted.TrySetResult();
                return releaseQuery.Task;
            }
        };
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("fallback", _ =>
                Task.FromResult(new InternalMicrosoftProbeResult(true, "fallback.alias", null)))]],
            probeStageTimeout: TimeSpan.FromMilliseconds(50),
            vsCodeMicrosoftAccountProvider: provider);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        await queryStarted.Task.DefaultTimeout();
        releaseQuery.SetResult(VsCodeMicrosoftAccountState.Unavailable);
        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("fallback.alias", result.Alias);
        var timeout = Assert.Single(result.ProbeDiagnostics, diagnostic => diagnostic.Source == "VS Code Microsoft tenant");
        Assert.Equal(InternalMicrosoftProbeOutcome.TimedOut, timeout.Outcome);
        Assert.Equal(InternalMicrosoftProbeFailureStage.ExtensionRpc, timeout.Failure?.Stage);
        Assert.Equal(InternalMicrosoftProbeExceptionType.TaskCanceled, timeout.Failure?.ExceptionType);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ContinuesOtherProbesWhenVsCodeProviderFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var provider = new TestVsCodeMicrosoftAccountProvider
        {
            GetInternalMicrosoftAccountAsyncCallback = _ => throw new NotSupportedException("Simulated older extension.")
        };
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("fallback", _ =>
                Task.FromResult(new InternalMicrosoftProbeResult(true, "fallback.alias", null)))]],
            vsCodeMicrosoftAccountProvider: provider);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("fallback.alias", result.Alias);
        var failure = Assert.Single(result.ProbeDiagnostics, diagnostic => diagnostic.Source == "VS Code Microsoft tenant");
        Assert.Equal(InternalMicrosoftProbeOutcome.Failed, failure.Outcome);
        Assert.Equal(InternalMicrosoftProbeFailureStage.ExtensionRpc, failure.Failure?.Stage);
        Assert.Equal(InternalMicrosoftProbeExceptionType.Other, failure.Failure?.ExceptionType);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_PropagatesCallerCancellationWithoutWritingCache()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestVsCodeMicrosoftAccountProvider
        {
            GetInternalMicrosoftAccountAsyncCallback = async cancellationToken =>
            {
                queryStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return VsCodeMicrosoftAccountState.Unavailable;
            }
        };
        var detector = CreateDetector(
            cacheFilePath,
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            vsCodeMicrosoftAccountProvider: provider);
        using var cancellationSource = new CancellationTokenSource();

        var detectionTask = detector.IsInternalMicrosoftMachineAsync(cancellationSource.Token);
        await queryStarted.Task.DefaultTimeout();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detectionTask);
        Assert.False(File.Exists(cacheFilePath));
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RefreshesFreshVsCodeCacheWhenAliasChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "VS Code Microsoft tenant",
              "alias": "old.alias",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("current VS Code account", _ =>
                Task.FromResult(new InternalMicrosoftProbeResult(true, "new.alias", null)))]],
            vsCodeMicrosoftAlias: "new.alias");

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
        Assert.Equal("new.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_UsesFreshNegativeCache()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": false,
              "isCIEnvironment": false,
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(cacheFilePath, now, []);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorOutcome.NotDetected, result.Outcome);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Hit, result.CacheStatus);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsProbesWhenCacheIsStaleAndUpdatesCache()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
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
                [new InternalMicrosoftProbe("positive", _ => Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "stale.alias", Domain: "STALE")))]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("stale.alias", result.Alias);
        Assert.Equal("STALE", result.Domain);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);

        var updatedCache = await File.ReadAllTextAsync(cacheFilePath);
        Assert.Contains("\"version\": 5", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"isInternalMicrosoft\": true", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"isCIEnvironment\": false", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"positive\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"alias\": \"stale.alias\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"domain\": \"STALE\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"lastRunUtc\": \"2026-06-16T12:00:00+00:00\"", updatedCache, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsNextStageOnlyWhenPreviousStageDoesNotDetect()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var calls = new List<string>();
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
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
                    return Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "stage.alias", Domain: "STAGE"));
                })],
                [new InternalMicrosoftProbe("stage 3", _ =>
                {
                    calls.Add("stage 3");
                    return Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "unused.alias", Domain: "UNUSED"));
                })]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("stage 2", result.Source);
        Assert.Equal("stage.alias", result.Alias);
        Assert.Equal("STAGE", result.Domain);
        Assert.Equal(["stage 1", "stage 2"], calls);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_StageTimeoutBoundsSlowProbeWhenFastProbeDetects()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var slowProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowProbeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("positive", async _ =>
                    {
                        await slowProbeStarted.Task;
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "positive.alias", Domain: "POSITIVE");
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
            ],
            probeStageTimeout: TimeSpan.FromSeconds(2));

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("positive.alias", result.Alias);
        Assert.Equal("POSITIVE", result.Domain);
        await slowProbeCancelled.Task.DefaultTimeout();
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "slow" && probe.Outcome == InternalMicrosoftProbeOutcome.TimedOut);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_IgnoresPositiveResultThatCompletesDuringCancellationDrain()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("late positive", async cancellationToken =>
            {
                using var registration = cancellationToken.Register(stageCancelled.SetResult);
                probeStarted.SetResult();
                await releaseProbe.Task;
                return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "late.alias", Domain: "LATE");
            })]],
            probeStageTimeout: TimeSpan.FromMilliseconds(50));

        var detectionTask = detector.IsInternalMicrosoftMachineAsync();
        await probeStarted.Task.DefaultTimeout();
        await stageCancelled.Task.DefaultTimeout();
        releaseProbe.SetResult();
        var result = await detectionTask;

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorOutcome.TimedOut, result.Outcome);
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "late positive" && probe.Outcome == InternalMicrosoftProbeOutcome.TimedOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsInternalMicrosoftMachineAsync_SelectsDeterministicStrongestResultRegardlessOfCompletionOrder(bool strongCompletesFirst)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("weak", async _ =>
                    {
                        if (!strongCompletesFirst)
                        {
                            releaseFirst.SetResult();
                        }
                        else
                        {
                            await releaseFirst.Task;
                        }
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: null, Domain: null);
                    }),
                    new InternalMicrosoftProbe("strong", async _ =>
                    {
                        if (strongCompletesFirst)
                        {
                            releaseFirst.SetResult();
                        }
                        else
                        {
                            await releaseFirst.Task;
                        }
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "strong.alias", Domain: "STRONG");
                    })
                ]
            ]);
        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("strong", result.Source);
        Assert.Equal("strong.alias", result.Alias);
        Assert.Equal("STRONG", result.Domain);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsLaterStagesWhenProbeThrowsUnexpectedException()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [new InternalMicrosoftProbe("faulting", _ => throw new NotSupportedException("Unexpected probe failure."))],
                [new InternalMicrosoftProbe("positive", _ => Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "later.alias", Domain: "LATER")))]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("later.alias", result.Alias);
        Assert.Equal("LATER", result.Domain);
        var failure = Assert.Single(result.ProbeDiagnostics, probe => probe.Source == "faulting");
        Assert.Equal(InternalMicrosoftProbeOutcome.Failed, failure.Outcome);
        Assert.Equal(InternalMicrosoftProbeFailureCode.Exception, failure.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.Probe, failure.Failure?.Stage);
        Assert.Equal(InternalMicrosoftProbeExceptionType.Other, failure.Failure?.ExceptionType);
        Assert.Null(failure.Failure?.ProcessExitCode);
        Assert.Null(failure.Failure?.HttpStatusCode);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReturnsNotDetectedOutcomeWhenNoProbeDetects()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("negative", _ => Task.FromResult(InternalMicrosoftProbeResult.NotDetected))]]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorOutcome.NotDetected, result.Outcome);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Miss, result.CacheStatus);
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "negative" && probe.Outcome == InternalMicrosoftProbeOutcome.NotDetected);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReturnsFailedOutcomeAndDoesNotCacheWhenAllProbesFail()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var detector = CreateDetector(
            cacheFilePath,
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("faulting", _ => throw new NotSupportedException("Unexpected probe failure."))]]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorOutcome.Failed, result.Outcome);
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "faulting" && probe.Outcome == InternalMicrosoftProbeOutcome.Failed);
        Assert.False(File.Exists(cacheFilePath));
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReportsElapsedDurationForDetectorWideFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 5,
              "isInternalMicrosoft": true,
              "isCIEnvironment": false,
              "source": "cached source",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [],
            timeProvider: new DelayedThrowingTimeProvider());

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.Equal(InternalMicrosoftDetectorOutcome.Failed, result.Outcome);
        Assert.True(result.Duration >= TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_DoesNotCacheNegativeWhenAnyProbeFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var detector = CreateDetector(
            cacheFilePath,
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[
                new InternalMicrosoftProbe("negative", _ => Task.FromResult(InternalMicrosoftProbeResult.NotDetected)),
                new InternalMicrosoftProbe("failed", _ => Task.FromResult(InternalMicrosoftProbeResult.Failed(new(
                    InternalMicrosoftProbeFailureCode.RequestFailed,
                    InternalMicrosoftProbeFailureStage.Probe))))
            ]]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.Equal(InternalMicrosoftDetectorOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(cacheFilePath));
        Assert.Contains(result.ProbeDiagnostics, diagnostic => diagnostic.Outcome == InternalMicrosoftProbeOutcome.NotDetected);
        Assert.Contains(result.ProbeDiagnostics, diagnostic => diagnostic.Outcome == InternalMicrosoftProbeOutcome.Failed);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReturnsTimedOutOutcomeWhenProbeStageTimesOut()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var detector = CreateDetector(
            cacheFilePath,
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("slow", async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                return InternalMicrosoftProbeResult.NotDetected;
            })]],
            probeStageTimeout: TimeSpan.FromMilliseconds(50));

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorOutcome.TimedOut, result.Outcome);
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "slow" && probe.Outcome == InternalMicrosoftProbeOutcome.TimedOut);
        Assert.False(File.Exists(cacheFilePath));
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReportsStageTimeoutDurationWhenProbeIgnoresCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeStageTimeout = TimeSpan.FromMilliseconds(50);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("hung", async _ =>
            {
                await releaseProbe.Task;
                return InternalMicrosoftProbeResult.NotDetected;
            })]],
            probeStageTimeout: probeStageTimeout);

        var result = await detector.IsInternalMicrosoftMachineAsync();
        releaseProbe.SetResult();

        var diagnostic = Assert.Single(result.ProbeDiagnostics);
        Assert.Equal("hung", diagnostic.Source);
        Assert.Equal(InternalMicrosoftProbeOutcome.TimedOut, diagnostic.Outcome);
        Assert.Equal(probeStageTimeout, diagnostic.Duration);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_TreatsUnknownCacheVersionAsStale()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 99,
              "isInternalMicrosoft": true,
              "source": "future source",
              "alias": "future.alias",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var probeRan = false;
        var detector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("current source", _ =>
            {
                probeRan = true;
                return Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "current.alias", Domain: "CURRENT"));
            })]]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(probeRan);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
        Assert.Equal("current source", result.Source);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_DoesNotReuseNegativeCacheAcrossCIAndLocalModes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var ciDetector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("CI negative", _ => Task.FromResult(InternalMicrosoftProbeResult.NotDetected))]],
            environmentVariables: new Dictionary<string, string?> { ["CI"] = "true" });

        var ciResult = await ciDetector.IsInternalMicrosoftMachineAsync();

        Assert.Equal(InternalMicrosoftDetectorOutcome.NotDetected, ciResult.Outcome);
        var ciCache = await File.ReadAllTextAsync(cacheFilePath);
        Assert.Contains("\"version\": 5", ciCache, StringComparison.Ordinal);
        Assert.Contains("\"isCIEnvironment\": true", ciCache, StringComparison.Ordinal);

        var localProbeRan = false;
        var localDetector = CreateDetector(
            cacheFilePath,
            now,
            [[new InternalMicrosoftProbe("local positive", _ =>
            {
                localProbeRan = true;
                return Task.FromResult(new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "local.alias", Domain: null));
            })]]);

        var localResult = await localDetector.IsInternalMicrosoftMachineAsync();

        Assert.True(localProbeRan);
        Assert.True(localResult.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, localResult.CacheStatus);
        Assert.Equal("local.alias", localResult.Alias);
    }

    [Theory]
    [InlineData("Visual Studio Microsoft tenant")]
    [InlineData("WSL Visual Studio Microsoft tenant")]
    public async Task IsInternalMicrosoftMachineAsync_RejectsLegacyVisualStudioCacheEntry(string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, $$"""
            {
              "isInternalMicrosoft": true,
              "source": "{{source}}",
              "alias": "Cached.Alias",
              "domain": "redmond.corp.microsoft.com",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(cacheFilePath, now, []);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
        Assert.Null(result.Alias);
        Assert.Null(result.Domain);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RejectsLegacyVsCodeCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "isInternalMicrosoft": true,
              "source": "VS Code Microsoft tenant",
              "alias": "ms-dotnettools.csdevkit-microsoftuser",
              "domain": "REDMOND",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(cacheFilePath, now, []);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Stale, result.CacheStatus);
        Assert.Null(result.Alias);
        Assert.Null(result.Domain);
    }

    [Fact]
    public async Task CheckWindowsUserDnsDomainAsync_UsesExecutionContextEnvironment()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: new Dictionary<string, string?>
            {
                ["USERDNSDOMAIN"] = "redmond.corp.microsoft.com",
                ["USERNAME"] = "test.alias"
            });

        var result = await detector.CheckWindowsUserDnsDomainAsync(CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("test.alias", result.Alias);
        Assert.Equal("REDMOND", result.Domain);
    }

    [Fact]
    public async Task CheckWindowsWorkplaceJoinAsync_UsesExecutionContextEnvironmentAndProcessFactory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "dsregcmd"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "dsregcmd.EXE"), string.Empty);
        var processFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0, """
                AzureAdJoined : YES
                WorkplaceJoined : NO
                TenantId : 72f988bf-86f1-41af-91ab-2d7cd011db47
                """)
        };
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            processFactory: processFactory,
            environmentVariables: new Dictionary<string, string?>
            {
                ["PATH"] = workspace.Path,
                ["PATHEXT"] = ".EXE",
                ["USERDNSDOMAIN"] = "redmond.corp.microsoft.com",
                ["USERNAME"] = "test.alias"
            });

        var result = await detector.CheckWindowsWorkplaceJoinAsync(CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("test.alias", result.Alias);
        Assert.Equal("REDMOND", result.Domain);
        Assert.Equal("dsregcmd", processFactory.LastFileName);
        var arguments = Assert.IsType<string[]>(processFactory.LastArguments);
        Assert.Equal(["/status"], arguments);
    }

    [Fact]
    public async Task CheckWindowsWorkplaceJoinAsync_ReturnsSafeFailureWhenProcessStartTimesOutInternally()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "dsregcmd"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "dsregcmd.EXE"), string.Empty);
        var processFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, workingDirectory, options) =>
                new StartCancellingProcessExecution(fileName, arguments, environment)
        };
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            processFactory: processFactory,
            environmentVariables: new Dictionary<string, string?>
            {
                ["PATH"] = workspace.Path,
                ["PATHEXT"] = ".EXE",
                ["USERDNSDOMAIN"] = "redmond.corp.microsoft.com",
                ["USERNAME"] = "test.alias"
            });

        var result = await detector.CheckWindowsWorkplaceJoinAsync(CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(InternalMicrosoftProbeFailureCode.ProcessTimeout, result.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.ProcessStart, result.Failure?.Stage);
        Assert.Null(result.Failure?.ExceptionType);
        Assert.Equal("dsregcmd", processFactory.LastFileName);
    }

    [Fact]
    public async Task CheckWindowsWorkplaceJoinAsync_ReturnsOnlyNonSensitiveProcessExitCode()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "dsregcmd"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "dsregcmd.EXE"), string.Empty);
        var processFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (7, "sensitive-host-name sensitive-user-path")
        };
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            processFactory: processFactory,
            environmentVariables: new Dictionary<string, string?>
            {
                ["PATH"] = workspace.Path,
                ["PATHEXT"] = ".EXE"
            });

        var result = await detector.CheckWindowsWorkplaceJoinAsync(CancellationToken.None);

        Assert.Equal(InternalMicrosoftProbeFailureCode.ProcessExit, result.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.ProcessExit, result.Failure?.Stage);
        Assert.Equal(7, result.Failure?.ProcessExitCode);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result.Failure), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsFailureWhenUserRequestIsUnauthorized()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/user" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckGitHubMembershipWithTokenResultForTestingAsync(CreateGitHubToken(1), CancellationToken.None);

        Assert.Equal(InternalMicrosoftProbeFailureCode.HttpStatus, result.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.GitHubUser, result.Failure?.Stage);
        Assert.Equal(401, result.Failure?.HttpStatusCode);
        Assert.Equal(["/user"], handler.GetRequestPaths());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(408)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsSafeHttpFailureMetadata(int statusCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckGitHubMembershipWithTokenResultForTestingAsync(CreateGitHubToken(1), CancellationToken.None);

        Assert.Equal(InternalMicrosoftProbeFailureCode.HttpStatus, result.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.GitHubUser, result.Failure?.Stage);
        Assert.Equal(statusCode, result.Failure?.HttpStatusCode);
        Assert.Null(result.Failure?.ExceptionType);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_DoesNotCacheUnauthorizedGitHubTokenResult()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        var handler = new TestGitHubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        InternalMicrosoftDetector? detector = null;
        detector = CreateDetector(
            cacheFilePath,
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [[new InternalMicrosoftProbe("github", cancellationToken =>
                detector!.CheckGitHubMembershipWithTokenResultForTestingAsync(CreateGitHubToken(1), cancellationToken))]],
            gitHubHttpMessageHandler: handler);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.Equal(InternalMicrosoftDetectorOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(cacheFilePath));
    }

    [Fact]
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsTrueForActivePrivateMembership()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/user" => JsonResponse(HttpStatusCode.OK, """{"login":"testuser"}"""),
                "/user/memberships/orgs/microsoft" => JsonResponse(HttpStatusCode.OK, """{"state":"active"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckGitHubMembershipWithTokenAsync(CreateGitHubToken(1), CancellationToken.None);

        Assert.True(result);
        Assert.Equal(["/user", "/user/memberships/orgs/microsoft"], handler.GetRequestPaths());
    }

    [Fact]
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsTrueForExplicitPublicMembership()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/user" => JsonResponse(HttpStatusCode.OK, """{"login":"testuser"}"""),
                "/user/memberships/orgs/microsoft" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/orgs/microsoft/public_members/testuser" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckGitHubMembershipWithTokenAsync(CreateGitHubToken(1), CancellationToken.None);

        Assert.True(result);
        Assert.Equal(["/user", "/user/memberships/orgs/microsoft", "/orgs/microsoft/public_members/testuser"], handler.GetRequestPaths());
    }

    [Fact]
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsFalseForNonMember()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/user" => JsonResponse(HttpStatusCode.OK, """{"login":"testuser"}"""),
                "/user/memberships/orgs/microsoft" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/orgs/microsoft/public_members/testuser" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckGitHubMembershipWithTokenAsync(CreateGitHubToken(1), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(["/user", "/user/memberships/orgs/microsoft", "/orgs/microsoft/public_members/testuser"], handler.GetRequestPaths());
    }

    [Fact]
    public async Task CheckCopilotCliAsync_ChecksTokenCandidatesWithoutCopilotCommand()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/user" => JsonResponse(HttpStatusCode.OK, """{"login":"testuser"}"""),
                "/user/memberships/orgs/microsoft" => JsonResponse(HttpStatusCode.OK, """{"state":"active"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: new Dictionary<string, string?>
            {
                ["PATH"] = workspace.Path,
                ["PATHEXT"] = ".EXE",
                ["COPILOT_GH_ACCOUNT_1"] = CreateGitHubToken(1)
            },
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckCopilotCliAsync(CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal(["/user", "/user/memberships/orgs/microsoft"], handler.GetRequestPaths());
    }

    [Fact]
    public async Task CheckCopilotCliAsync_LimitsGitHubTokenCandidates()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/user" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var environmentVariables = Enumerable.Range(0, 7)
            .ToDictionary(index => $"COPILOT_GH_ACCOUNT_{index}", index => (string?)CreateGitHubToken(index));
        environmentVariables["PATH"] = workspace.Path;
        environmentVariables["PATHEXT"] = ".EXE";
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: environmentVariables,
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckCopilotCliAsync(CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(5, handler.GetRequestPaths().Count(path => path == "/user"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("2")]
    public async Task CheckCopilotCliAsync_SkipsGitHubTokenCandidatesInCI(string ciValue)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: new Dictionary<string, string?>
            {
                ["CI"] = ciValue,
                ["COPILOT_GH_ACCOUNT_1"] = CreateGitHubToken(1)
            },
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckCopilotCliAsync(CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Empty(handler.GetRequestPaths());
    }

    [Fact]
    public async Task CheckVsCodeMicrosoftAccountAsync_UsesExtensionProvidedAlias()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            vsCodeMicrosoftAlias: "current.alias");

        var result = await detector.CheckVsCodeMicrosoftAccountAsync(CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public async Task CheckVsCodeMicrosoftAccountAsync_ReturnsNotDetectedWithoutExtensionSignal()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: []);

        var result = await detector.CheckVsCodeMicrosoftAccountAsync(CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_DefaultProbesUseAvailableVsCodeAccount()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: null,
            environment: TestEnvironment.CreateLinux(new Dictionary<string, string?> { ["CI"] = "true" }),
            vsCodeMicrosoftAlias: "current.alias");

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("VS Code Microsoft tenant", result.Source);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_DefaultProbesOmitUnavailableVsCodeAccount()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: null,
            environment: TestEnvironment.CreateLinux(new Dictionary<string, string?> { ["CI"] = "true" }));

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.False(result.IsInternalMicrosoft);
        Assert.Empty(result.ProbeDiagnostics);
    }

    [Fact]
    public void DetectVisualStudioMicrosoftTenantForTesting_PrefersPersonalizationAccount()
    {
        var store = CreateVisualStudioAccountStore(
            new VisualStudioAccountRecord("fallback.alias@microsoft.com"),
            new VisualStudioAccountRecord("Current.Alias@microsoft.com", IsPersonalizationAccount: true));

        var result = InternalMicrosoftDetector.DetectVisualStudioMicrosoftTenantForTesting(
            store,
            CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public void DetectVisualStudioMicrosoftTenantForTesting_UsesNonPersonalizationAccountAsFallback()
    {
        var result = InternalMicrosoftDetector.DetectVisualStudioMicrosoftTenantForTesting(
            CreateVisualStudioAccountStore(new VisualStudioAccountRecord("Fallback.Alias@microsoft.com")),
            CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("fallback.alias", result.Alias);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("identity-provider")]
    [InlineData("home-tenant")]
    [InlineData("token-tenant")]
    [InlineData("token-issuer")]
    [InlineData("preferred-username")]
    [InlineData("preferred-username-substring")]
    [InlineData("token-payload")]
    public void DetectVisualStudioMicrosoftTenantForTesting_RejectsIncompleteOrMismatchedEvidence(string mismatch)
    {
        const string OtherTenantId = "11111111-1111-1111-1111-111111111111";
        var account = mismatch switch
        {
            "stale" => new VisualStudioAccountRecord("user@microsoft.com", Stale: true),
            "identity-provider" => new VisualStudioAccountRecord("user@microsoft.com", IdentityProvider: OtherTenantId),
            "home-tenant" => new VisualStudioAccountRecord("user@microsoft.com", HomeTenant: OtherTenantId),
            "token-tenant" => new VisualStudioAccountRecord("user@microsoft.com", TokenTenant: OtherTenantId),
            "token-issuer" => new VisualStudioAccountRecord("user@microsoft.com", TokenIssuer: $"https://login.microsoftonline.com/{OtherTenantId}/v2.0"),
            "preferred-username" => new VisualStudioAccountRecord("user@example.com"),
            "preferred-username-substring" => new VisualStudioAccountRecord("display user@microsoft.com text"),
            "token-payload" => new VisualStudioAccountRecord(
                "user@microsoft.com",
                IdTokenPayload: CreateJwt(MicrosoftTenantIdForTests, "user@microsoft.com")),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        var result = InternalMicrosoftDetector.DetectVisualStudioMicrosoftTenantForTesting(
            CreateVisualStudioAccountStore(account),
            CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Null(result.Alias);
        if (mismatch is "preferred-username" or "preferred-username-substring")
        {
            Assert.Equal(InternalMicrosoftProbeFailureCode.JsonShape, result.Failure?.Code);
            Assert.Equal(InternalMicrosoftProbeFailureStage.IdTokenUsername, result.Failure?.Stage);
        }
        else if (mismatch == "token-payload")
        {
            Assert.Equal(InternalMicrosoftProbeFailureCode.JsonParse, result.Failure?.Code);
            Assert.Equal(InternalMicrosoftProbeFailureStage.IdTokenPayload, result.Failure?.Stage);
            Assert.Equal(InternalMicrosoftProbeExceptionType.Json, result.Failure?.ExceptionType);
        }
        else
        {
            Assert.Null(result.Failure);
        }
    }

    [Fact]
    public void DetectVisualStudioMicrosoftTenantForTesting_DoesNotMatchUnrelatedTenantTextOrAlias()
    {
        var store = $$"""
            [
              {
                "Stale": false,
                "IsPersonalizationAccount": true,
                "DisplayInfo": "wrong.alias@microsoft.com",
                "Properties": {
                  "UnrelatedTenant": "{{MicrosoftTenantIdForTests}}"
                }
              }
            ]
            """;

        var result = InternalMicrosoftDetector.DetectVisualStudioMicrosoftTenantForTesting(store, CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Null(result.Alias);
        Assert.Equal(InternalMicrosoftProbeFailureCode.JsonShape, result.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.AccountStoreRecordIdentityProvider, result.Failure?.Stage);
    }

    [Fact]
    public void DetectVisualStudioMicrosoftTenantForTesting_ReturnsSafeJsonParseFailure()
    {
        var result = InternalMicrosoftDetector.DetectVisualStudioMicrosoftTenantForTesting(
            """[{"Properties":{"preferred_username":"sensitive.user@microsoft.com"}}""",
            CancellationToken.None);

        Assert.Equal(InternalMicrosoftProbeFailureCode.JsonParse, result.Failure?.Code);
        Assert.Equal(InternalMicrosoftProbeFailureStage.AccountStore, result.Failure?.Stage);
        Assert.Equal(InternalMicrosoftProbeExceptionType.Json, result.Failure?.ExceptionType);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result.Failure), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckCopilotCliAsync_UsesOverallGitHubTokenCandidateTimeout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var handler = new TestGitHubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var environmentVariables = Enumerable.Range(0, 7)
            .ToDictionary(index => $"COPILOT_GH_ACCOUNT_{index}", index => (string?)CreateGitHubToken(index));
        environmentVariables["PATH"] = workspace.Path;
        environmentVariables["PATHEXT"] = ".EXE";
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: environmentVariables,
            gitHubHttpMessageHandler: handler,
            gitHubCandidateTimeout: TimeSpan.FromMilliseconds(100),
            // HttpClient.Timeout defaults to 3 seconds here, which would independently cancel every
            // probe well inside the assertion bound below and make this test pass even with no candidate
            // budget at all. Disabling the per-request timeout leaves the overall budget as the only
            // thing that can stop the handler's one-minute delay, so the assertion measures what it claims.
            gitHubHttpTimeout: Timeout.InfiniteTimeSpan);

        var stopwatch = Stopwatch.StartNew();
        var result = await detector.CheckCopilotCliAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.False(result.IsInternalMicrosoft);

        // The handler blocks for a minute per request and HttpClient.Timeout is disabled above, so an
        // unenforced candidate budget takes at least a minute. Ten seconds leaves ample cancellation,
        // drain, and scheduler headroom while still proving the overall budget stops the probes. The
        // previous two-second bound failed at 2.064s on a loaded windows-latest runner:
        // https://github.com/microsoft/aspire/issues/19181.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Elapsed {stopwatch.Elapsed} exceeded the overall candidate timeout budget.");
        Assert.Equal(5, handler.GetRequestPaths().Count(path => path == "/user"));
    }

    [Fact]
    public async Task CheckCopilotCliAsync_ProbesGitHubTokenCandidatesConcurrently()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const int candidateCount = 5;
        var allCandidatesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCandidates = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCandidates = 0;
        var handler = new TestGitHubHttpMessageHandler(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref enteredCandidates) == candidateCount)
            {
                allCandidatesEntered.TrySetResult();
            }

            await releaseCandidates.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var environmentVariables = Enumerable.Range(0, 7)
            .ToDictionary(index => $"COPILOT_GH_ACCOUNT_{index}", index => (string?)CreateGitHubToken(index));
        environmentVariables["PATH"] = workspace.Path;
        environmentVariables["PATHEXT"] = ".EXE";
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: environmentVariables,
            gitHubHttpMessageHandler: handler,
            gitHubCandidateTimeout: Timeout.InfiniteTimeSpan,
            gitHubHttpTimeout: Timeout.InfiniteTimeSpan);

        using var safetyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var checkTask = detector.CheckCopilotCliAsync(safetyTimeout.Token);

        // The test releases the handlers only after all five have entered. A serial implementation
        // cannot reach that point; the independent timeout keeps that regression from hanging the suite.
        try
        {
            await allCandidatesEntered.Task.WaitAsync(safetyTimeout.Token);
        }
        finally
        {
            releaseCandidates.TrySetResult();
        }

        var result = await checkTask.WaitAsync(safetyTimeout.Token);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Equal(candidateCount, enteredCandidates);
        Assert.Equal(candidateCount, handler.GetRequestPaths().Count(path => path == "/user"));
    }

    private static InternalMicrosoftDetector CreateDetector(
        string cacheFilePath,
        DateTimeOffset now,
        IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>>? probeStages,
        TestProcessExecutionFactory? processFactory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        HttpMessageHandler? gitHubHttpMessageHandler = null,
        TimeSpan? gitHubCandidateTimeout = null,
        TimeSpan? gitHubHttpTimeout = null,
        TimeSpan? probeStageTimeout = null,
        TestEnvironment? environment = null,
        DirectoryInfo? homeDirectory = null,
        string? vsCodeMicrosoftAlias = null,
        bool vsCodeMicrosoftProviderAvailable = false,
        TestVsCodeMicrosoftAccountProvider? vsCodeMicrosoftAccountProvider = null,
        TimeProvider? timeProvider = null)
    {
        var executionContext = Utils.TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(Path.GetDirectoryName(cacheFilePath) ?? AppContext.BaseDirectory),
            homeDirectory: homeDirectory);
        var effectiveEnvironment = environment ?? new TestEnvironment(environmentVariables);
        var ciEnvironmentDetector = new CIEnvironmentDetector(
            new ConfigurationBuilder()
                .AddInMemoryCollection(effectiveEnvironment.Variables)
                .Build());

        return new InternalMicrosoftDetector(
            executionContext,
            effectiveEnvironment,
            cacheFilePath,
            timeProvider ?? new FixedTimeProvider(now),
            NullLogger<InternalMicrosoftDetector>.Instance,
            processFactory ?? new TestProcessExecutionFactory(),
            ciEnvironmentDetector,
            vsCodeMicrosoftAccountProvider ?? new TestVsCodeMicrosoftAccountProvider(
                isAvailable: vsCodeMicrosoftProviderAvailable || vsCodeMicrosoftAlias is not null,
                alias: vsCodeMicrosoftAlias),
            probeStages,
            gitHubHttpMessageHandler,
            gitHubCandidateTimeout,
            gitHubHttpTimeout,
            probeStageTimeout);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateGitHubToken(int index)
        => $"gho_{index:D2}{new string('a', 24)}";

    private const string MicrosoftTenantIdForTests = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    private static string CreateJwt(string tenantId, string userName)
    {
        var payload = JsonSerializer.Serialize(new { tid = tenantId, preferred_username = userName });
        return $"eyJ0eXAiOiJKV1Q.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}.signature";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreateVisualStudioAccountStore(params VisualStudioAccountRecord[] accounts)
    {
        return JsonSerializer.Serialize(accounts.Select(account => new
        {
            account.Stale,
            account.IsPersonalizationAccount,
            DisplayInfo = account.PreferredUsername,
            Properties = new
            {
                IdentityProvider = account.IdentityProvider ?? MicrosoftTenantIdForTests,
                HomeTenant = account.HomeTenant ?? MicrosoftTenantIdForTests,
                IdTokenPayload = account.IdTokenPayload ?? JsonSerializer.Serialize(new
                {
                    tid = account.TokenTenant ?? MicrosoftTenantIdForTests,
                    iss = account.TokenIssuer ?? $"https://login.microsoftonline.com/{MicrosoftTenantIdForTests}/v2.0",
                    preferred_username = account.PreferredUsername
                })
            }
        }));
    }

    private sealed record VisualStudioAccountRecord(
        string PreferredUsername,
        bool IsPersonalizationAccount = false,
        bool Stale = false,
        string? IdentityProvider = null,
        string? HomeTenant = null,
        string? TokenTenant = null,
        string? TokenIssuer = null,
        string? IdTokenPayload = null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DelayedThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            Thread.Sleep(20);
            throw new InvalidOperationException("Simulated detector-wide failure.");
        }
    }

    private sealed class StartCancellingProcessExecution(
        string fileName,
        IReadOnlyList<string> arguments,
        IDictionary<string, string>? environment) : IProcessExecution
    {
        public string FileName { get; } = fileName;

        public IReadOnlyList<string> Arguments { get; } = arguments;

        public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; } =
            environment?.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value)
            ?? new Dictionary<string, string?>();

        public int ProcessId => Environment.ProcessId;

        public DateTimeOffset? StartTime => DateTimeOffset.UtcNow;

        public bool HasExited => false;

        public int ExitCode => 0;

        public Task<bool> StartAsync(CancellationToken cancellationToken)
            => throw new OperationCanceledException(cancellationToken);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("The process should not wait after start cancellation.");

        public void Kill(bool entireProcessTree)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestGitHubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<string> _requestPaths = [];

        public IReadOnlyList<string> GetRequestPaths()
        {
            lock (_lock)
            {
                return [.. _requestPaths];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _requestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            }

            return await sendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
