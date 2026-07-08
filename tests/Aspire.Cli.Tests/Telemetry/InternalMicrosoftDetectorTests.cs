// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace Aspire.Cli.Tests.Telemetry;

public sealed class InternalMicrosoftDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_UsesFreshCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        Assert.False(probeRan);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsProbesWhenCacheIsStaleAndUpdatesCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var updatedCache = await File.ReadAllTextAsync(cacheFilePath);
        Assert.Contains("\"isInternalMicrosoft\": true", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"positive\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"alias\": \"stale.alias\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"domain\": \"STALE\"", updatedCache, StringComparison.Ordinal);
        Assert.Contains("\"lastRunUtc\": \"2026-06-16T12:00:00+00:00\"", updatedCache, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsNextStageOnlyWhenPreviousStageDoesNotDetect()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
    public async Task IsInternalMicrosoftMachineAsync_CancelsOtherProbesInStageAfterSuccessfulProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
            ]);

        var result = await detector.IsInternalMicrosoftMachineAsync();

        Assert.True(result.IsInternalMicrosoft);
        Assert.Equal("positive", result.Source);
        Assert.Equal("positive.alias", result.Alias);
        Assert.Equal("POSITIVE", result.Domain);
        await slowProbeCancelled.Task.DefaultTimeout();
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_ReturnsPositiveResultWhenCancelledProbeFaultsDuringDrain()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var faultingProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = CreateDetector(
            Path.Combine(workspace.Path, "cache", "detector.json"),
            new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
            [
                [
                    new InternalMicrosoftProbe("positive", async _ =>
                    {
                        await faultingProbeStarted.Task;
                        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: "fault.alias", Domain: "FAULT");
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
        Assert.Equal("FAULT", result.Domain);
    }

    [Fact]
    public async Task IsInternalMicrosoftMachineAsync_RunsLaterStagesWhenProbeThrowsUnexpectedException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
    }

    [Fact]
    public async Task CheckWindowsUserDnsDomainAsync_UsesExecutionContextEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
    public async Task CheckGitHubMembershipWithTokenAsync_ReturnsFalseWhenUserRequestFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
    public async Task CheckCopilotCliAsync_UsesOverallGitHubTokenCandidateTimeout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
            gitHubCandidateTimeout: TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        var result = await detector.CheckCopilotCliAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.False(result.IsInternalMicrosoft);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(5, handler.GetRequestPaths().Count(path => path == "/user"));
    }

    private static InternalMicrosoftDetector CreateDetector(
        string cacheFilePath,
        DateTimeOffset now,
        IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> probeStages,
        TestProcessExecutionFactory? processFactory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        HttpMessageHandler? gitHubHttpMessageHandler = null,
        TimeSpan? gitHubCandidateTimeout = null)
    {
        var executionContext = Utils.TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(Path.GetDirectoryName(cacheFilePath) ?? AppContext.BaseDirectory));

        return new InternalMicrosoftDetector(
            executionContext,
            new TestEnvironment(environmentVariables),
            cacheFilePath,
            new FixedTimeProvider(now),
            NullLogger<InternalMicrosoftDetector>.Instance,
            processFactory ?? new TestProcessExecutionFactory(),
            probeStages,
            gitHubHttpMessageHandler,
            gitHubCandidateTimeout);
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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