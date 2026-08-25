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
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

public sealed class InternalMicrosoftDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_UsesFreshCache()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "isInternalMicrosoft": true,
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
    public async Task IsInternalMicrosoftMachineAsync_UsesFreshNegativeCache()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "version": 1,
              "isInternalMicrosoft": false,
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
        Assert.Contains("\"version\": 1", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"isInternalMicrosoft\": true", updatedCache, StringComparison.Ordinal);
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
        var slowProbeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("positive", async _ =>
                    {
                        await Task.Yield();
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "positive.alias", Domain: "POSITIVE");
                    }),
                    new InternalMicrosoftProbe("slow", async cancellationToken =>
                    {
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
            probeStageTimeout: TimeSpan.FromMilliseconds(50));

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("positive.alias", result.Alias);
        Assert.Equal("POSITIVE", result.Domain);
        await slowProbeCancelled.Task.DefaultTimeout();
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "slow" && probe.Outcome == InternalMicrosoftProbeOutcome.TimedOut);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_SelectsDeterministicStrongestResultRegardlessOfCompletionOrder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var releaseWeak = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStrong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("weak", async _ =>
                    {
                        releaseWeak.SetResult();
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: null, Domain: null);
                    }),
                    new InternalMicrosoftProbe("strong", async _ =>
                    {
                        await releaseWeak.Task;
                        releaseStrong.SetResult();
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "strong.alias", Domain: "STRONG");
                    })
                ]
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        await releaseStrong.Task.DefaultTimeout();
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
        Assert.Contains(result.ProbeDiagnostics, probe => probe.Source == "faulting" && probe.Outcome == InternalMicrosoftProbeOutcome.Failed);
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
    public async Task IsInternalMicrosoftMachineAsync_CanonicalizesLegacyCachedAliases()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var now = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var cacheFilePath = Path.Combine(workspace.Path, "cache", "detector.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, """
            {
              "isInternalMicrosoft": true,
              "source": "Visual Studio Microsoft tenant",
              "alias": "Cached.Alias",
              "domain": "redmond.corp.microsoft.com",
              "lastRunUtc": "2026-06-16T11:00:00+00:00"
            }
            """);
        var detector = CreateDetector(cacheFilePath, now, []);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("cached.alias", result.Alias);
        Assert.Equal("REDMOND", result.Domain);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_DropsLegacyVsCodeCachedAlias()
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

        Assert.True(result.IsInternalMicrosoft);
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
    public async Task CheckWindowsWorkplaceJoinAsync_ReturnsNotDetectedWhenProcessStartTimesOutInternally()
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
        Assert.Equal("dsregcmd", processFactory.LastFileName);
    }

    [Fact]
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsFalseWhenUserRequestFails()
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

        var result = await detector.CheckGitHubMembershipWithTokenAsync(CreateGitHubToken(1), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(["/user"], handler.GetRequestPaths());
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

    [Fact]
    public async Task CheckCopilotCliAsync_SkipsGitHubTokenCandidatesInCI()
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
                ["CI"] = "true",
                ["COPILOT_GH_ACCOUNT_1"] = CreateGitHubToken(1)
            },
            gitHubHttpMessageHandler: handler);

        var result = await detector.CheckCopilotCliAsync(CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
        Assert.Empty(handler.GetRequestPaths());
    }

    [Fact]
    public async Task CheckVsCodeMicrosoftTenantAsync_ReadsOnlyLogicalSqliteTextValues()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appData = Path.Combine(workspace.Path, "appdata");
        var stateDatabasePath = Path.Combine(appData, "Code", "User", "globalStorage", "state.vscdb");
        Directory.CreateDirectory(Path.GetDirectoryName(stateDatabasePath)!);
        await File.WriteAllBytesAsync(stateDatabasePath, CreateSqliteDatabase(
            "ms-dotnettools.csdevkit-microsoft",
            $"USER@MICROSOFT.COM {CreateJwt(MicrosoftTenantIdForTests, "user@microsoft.com")}"));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environmentVariables: new Dictionary<string, string?>
            {
                ["APPDATA"] = appData
            },
            environment: TestEnvironment.CreateWindows(new Dictionary<string, string?>
            {
                ["APPDATA"] = appData
            }));

        var result = await detector.CheckVsCodeMicrosoftTenantAsync(CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("user", result.Alias);
    }

    [Fact]
    public async Task CheckVsCodeMicrosoftTenantAsync_RejectsStorageKeyAdjacencyWithoutTenantEvidence()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appData = Path.Combine(workspace.Path, "appdata");
        var stateDatabasePath = Path.Combine(appData, "Code", "User", "globalStorage", "state.vscdb");
        Directory.CreateDirectory(Path.GetDirectoryName(stateDatabasePath)!);
        await File.WriteAllBytesAsync(stateDatabasePath, CreateSqliteDatabase("ms-dotnettools.csdevkit-microsoft", "user@microsoft.com"));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environment: TestEnvironment.CreateWindows(new Dictionary<string, string?>
            {
                ["APPDATA"] = appData
            }));

        var result = await detector.CheckVsCodeMicrosoftTenantAsync(CancellationToken.None);

        Assert.False(result.IsInternalMicrosoft);
    }

    [Fact]
    public async Task CheckVsCodeMicrosoftTenantAsync_UsesCurrentTenantBoundValueWhenStaleAccountExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appData = Path.Combine(workspace.Path, "appdata");
        var stateDatabasePath = Path.Combine(appData, "Code", "User", "globalStorage", "state.vscdb");
        Directory.CreateDirectory(Path.GetDirectoryName(stateDatabasePath)!);
        await File.WriteAllBytesAsync(stateDatabasePath, CreateSqliteDatabase(
            "old.alias@microsoft.com",
            $"Current.Alias@microsoft.com {CreateJwt(MicrosoftTenantIdForTests, "Current.Alias@microsoft.com")}"));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environment: TestEnvironment.CreateWindows(new Dictionary<string, string?>
            {
                ["APPDATA"] = appData
            }));

        var result = await detector.CheckVsCodeMicrosoftTenantAsync(CancellationToken.None);

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("current.alias", result.Alias);
    }

    [Fact]
    public void ExtractSqliteRecordTextValuesForTesting_HandlesVarintsAndSerialTypes()
    {
        var values = InternalMicrosoftDetector.ExtractSqliteRecordTextValuesForTesting(CreateSqliteDatabase(new string('a', 140)), CancellationToken.None);

        var value = Assert.Single(values);
        Assert.Equal(new string('a', 140), value);
    }

    [Fact]
    public void ExtractSqliteRecordTextValuesForTesting_FailsClosedForTruncatedPayload()
    {
        var database = CreateSqliteDatabase("user@microsoft.com");
        var cellOffset = (database[108] << 8) | database[109];
        database[cellOffset] = 0x7F;

        var values = InternalMicrosoftDetector.ExtractSqliteRecordTextValuesForTesting(database, CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public void ExtractSqliteRecordTextValuesForTesting_RejectsInvalidPageSize()
    {
        var database = CreateSqliteDatabase("user@microsoft.com");
        database[16] = 0x03;
        database[17] = 0x00;

        var values = InternalMicrosoftDetector.ExtractSqliteRecordTextValuesForTesting(database, CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public void ExtractSqliteRecordTextValuesForTesting_SkipsFreelistLeafPages()
    {
        const int pageSize = 512;
        var database = new byte[pageSize * 4];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(database, 0);
        database[16] = 0x02;
        database[17] = 0x00;
        WriteUInt32BigEndian(database, 32, 2);
        WriteUInt32BigEndian(database, 36, 2);

        WriteUInt32BigEndian(database, pageSize + 4, 1);
        WriteUInt32BigEndian(database, pageSize + 8, 3);
        WriteSqliteLeafPage(database, pageSize * 2, 0, "deleted.user@microsoft.com");
        WriteSqliteLeafPage(database, pageSize * 3, 0, "active.user@microsoft.com");

        var values = InternalMicrosoftDetector.ExtractSqliteRecordTextValuesForTesting(database, CancellationToken.None);

        Assert.Equal(["active.user@microsoft.com"], values);
    }

    [Fact]
    public void ExtractSqliteRecordTextValuesForTesting_SkipsOverflowPayloads()
    {
        var values = InternalMicrosoftDetector.ExtractSqliteRecordTextValuesForTesting(CreateSqliteDatabaseWithOverflow(new string('a', 600)), CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public void ExtractSqliteRecordTextValuesForTesting_RejectsCellPointerBeforeCellContentArea()
    {
        var database = CreateSqliteDatabase("user@microsoft.com");
        var cellOffset = (database[108] << 8) | database[109];
        var cellLength = database.Length - cellOffset;
        const int corruptCellOffset = 110;
        Array.Copy(database, cellOffset, database, corruptCellOffset, cellLength);
        database[108] = 0;
        database[109] = corruptCellOffset;

        var values = InternalMicrosoftDetector.ExtractSqliteRecordTextValuesForTesting(database, CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public void GetVsCodeStateDatabasePathsForTesting_ReturnsWindowsProductPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appData = Path.Combine(workspace.Path, "appdata");
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environment: TestEnvironment.CreateWindows(new Dictionary<string, string?>
            {
                ["APPDATA"] = appData
            }));

        Assert.Equal(
            [
                Path.Combine(appData, "Code", "User", "globalStorage", "state.vscdb"),
                Path.Combine(appData, "Code - Insiders", "User", "globalStorage", "state.vscdb"),
                Path.Combine(appData, "VSCodium", "User", "globalStorage", "state.vscdb")
            ],
            detector.GetVsCodeStateDatabasePathsForTesting());
    }

    [Fact]
    public void GetVsCodeStateDatabasePathsForTesting_ReturnsLinuxAndWslPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = new DirectoryInfo(Path.Combine(workspace.Path, "home"));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environment: TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["WSL_DISTRO_NAME"] = "Ubuntu"
            }),
            homeDirectory: home);

        Assert.Equal(
            [
                Path.Combine(home.FullName, ".config", "Code", "User", "globalStorage", "state.vscdb"),
                Path.Combine(home.FullName, ".config", "Code - Insiders", "User", "globalStorage", "state.vscdb"),
                Path.Combine(home.FullName, ".config", "VSCodium", "User", "globalStorage", "state.vscdb"),
                Path.Combine(home.FullName, ".vscode-server", "data", "User", "globalStorage", "state.vscdb"),
                Path.Combine(home.FullName, ".vscode-server-insiders", "data", "User", "globalStorage", "state.vscdb")
            ],
            detector.GetVsCodeStateDatabasePathsForTesting());
    }

    [Fact]
    public void GetVsCodeStateDatabasePathsForTesting_ReturnsMacOSProductPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = new DirectoryInfo(Path.Combine(workspace.Path, "home"));
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            probeStages: [],
            environment: TestEnvironment.CreateMacOS(),
            homeDirectory: home);

        Assert.Equal(
            [
                Path.Combine(home.FullName, "Library", "Application Support", "Code", "User", "globalStorage", "state.vscdb"),
                Path.Combine(home.FullName, "Library", "Application Support", "Code - Insiders", "User", "globalStorage", "state.vscdb"),
                Path.Combine(home.FullName, "Library", "Application Support", "VSCodium", "User", "globalStorage", "state.vscdb")
            ],
            detector.GetVsCodeStateDatabasePathsForTesting());
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
        IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> probeStages,
        TestProcessExecutionFactory? processFactory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        HttpMessageHandler? gitHubHttpMessageHandler = null,
        TimeSpan? gitHubCandidateTimeout = null,
        TimeSpan? gitHubHttpTimeout = null,
        TimeSpan? probeStageTimeout = null,
        TestEnvironment? environment = null,
        DirectoryInfo? homeDirectory = null)
    {
        var executionContext = Utils.TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(Path.GetDirectoryName(cacheFilePath) ?? AppContext.BaseDirectory),
            homeDirectory: homeDirectory);

        return new InternalMicrosoftDetector(
            executionContext,
            environment ?? new TestEnvironment(environmentVariables),
            cacheFilePath,
            new FixedTimeProvider(now),
            NullLogger<InternalMicrosoftDetector>.Instance,
            processFactory ?? new TestProcessExecutionFactory(),
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

    private static byte[] CreateSqliteDatabase(params string[] values)
    {
        const int pageSize = 512;
        var database = new byte[pageSize];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(database, 0);
        database[16] = 0x02;
        database[17] = 0x00;
        WriteSqliteLeafPage(database, pageOffset: 0, headerOffsetInPage: 100, values);
        return database;
    }

    private static void WriteSqliteLeafPage(byte[] database, int pageOffset, int headerOffsetInPage, params string[] values)
    {
        const int pageSize = 512;
        var headerOffset = pageOffset + headerOffsetInPage;
        database[headerOffset] = 0x0D;
        database[headerOffset + 3] = 0x00;
        database[headerOffset + 4] = 0x01;
        var payload = CreateSqliteRecordPayload(values);
        var cell = new List<byte>();
        WriteSqliteVarint(cell, payload.Count);
        WriteSqliteVarint(cell, 1);
        cell.AddRange(payload);

        var cellOffset = pageSize - cell.Count;
        database[headerOffset + 5] = (byte)(cellOffset >> 8);
        database[headerOffset + 6] = (byte)cellOffset;
        database[headerOffset + 8] = (byte)(cellOffset >> 8);
        database[headerOffset + 9] = (byte)cellOffset;
        cell.CopyTo(database, pageOffset + cellOffset);
    }

    private static byte[] CreateSqliteDatabaseWithOverflow(string value)
    {
        const int pageSize = 512;
        const int usableSize = pageSize;
        const int leafPageNumber = 2;
        const int overflowPageNumber = 3;
        var database = new byte[pageSize * overflowPageNumber];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(database, 0);
        database[16] = 0x02;
        database[17] = 0x00;

        var payload = CreateSqliteRecordPayload([value]);
        var minimumLocalPayload = (((usableSize - 12) * 32) / 255) - 23;
        var maximumLocalPayload = usableSize - 35;
        var localPayloadLength = minimumLocalPayload + ((payload.Count - minimumLocalPayload) % (usableSize - 4));
        if (localPayloadLength > maximumLocalPayload)
        {
            localPayloadLength = minimumLocalPayload;
        }

        var cell = new List<byte>();
        WriteSqliteVarint(cell, payload.Count);
        WriteSqliteVarint(cell, 1);
        cell.AddRange(payload.Take(localPayloadLength));
        cell.Add(0);
        cell.Add(0);
        cell.Add(0);
        cell.Add(overflowPageNumber);

        var leafPageOffset = (leafPageNumber - 1) * pageSize;
        database[leafPageOffset] = 0x0D;
        database[leafPageOffset + 3] = 0;
        database[leafPageOffset + 4] = 1;
        var cellOffsetInPage = pageSize - cell.Count;
        database[leafPageOffset + 5] = (byte)(cellOffsetInPage >> 8);
        database[leafPageOffset + 6] = (byte)cellOffsetInPage;
        database[leafPageOffset + 8] = (byte)(cellOffsetInPage >> 8);
        database[leafPageOffset + 9] = (byte)cellOffsetInPage;
        cell.CopyTo(database, leafPageOffset + cellOffsetInPage);

        var overflowPageOffset = (overflowPageNumber - 1) * pageSize;
        payload.Skip(localPayloadLength).ToArray().CopyTo(database, overflowPageOffset + 4);
        return database;
    }

    private static List<byte> CreateSqliteRecordPayload(string[] values)
    {
        var serialTypes = new List<byte>();
        var valueBytes = new List<byte>();
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteSqliteVarint(serialTypes, 13 + (bytes.Length * 2));
            valueBytes.AddRange(bytes);
        }

        var header = new List<byte>();
        WriteSqliteVarint(header, 1 + serialTypes.Count);
        header.AddRange(serialTypes);
        header.AddRange(valueBytes);
        return header;
    }

    private static void WriteSqliteVarint(List<byte> bytes, int value)
    {
        if (value < 0x80)
        {
            bytes.Add((byte)value);
            return;
        }

        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0)
        {
            stack.Push((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        bytes.AddRange(stack);
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
