// <copyright file="ChaosRandomFaultMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Policy.Profiles;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosRandomFaultMiddlewareTests
{
    private static FaultProfileRegistry SingleErrorProfile(int status = 503) =>
        FaultProfileRegistry.FromProfiles(new FaultProfile
        {
            Id = "test.single-error",
            Entries = new[] { new FaultProfileEntry { Weight = 1.0, Kind = "error", Status = status, Body = "boom", ContentType = "text/plain" } },
        });

    private static FaultProfileRegistry TwoErrorProfile() =>
        FaultProfileRegistry.FromProfiles(new FaultProfile
        {
            Id = "test.two-error",
            Entries = new[]
            {
                new FaultProfileEntry { Weight = 0.5, Kind = "error", Status = 500 },
                new FaultProfileEntry { Weight = 0.5, Kind = "error", Status = 503 },
            },
        });

    private static ActivePolicy RandomPolicy(
        string profileId,
        double intensity,
        int seed = 1234,
        int? maxFires = null,
        IReadOnlyList<string>? excludePaths = null,
        RequestMatcher? matcher = null,
        string id = "rnd") =>
        new(
            Id: id,
            Matcher: matcher,
            Latency: null,
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null,
            ForwardThenFail: null,
            RandomFault: new RandomFaultConfig(profileId, intensity, seed, maxFires, excludePaths));

    [Fact]
    public async Task NoRandomPolicy_ForwardsToUpstream()
    {
        await using var fx = new ChaosPipelineFixture();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task IntensityOne_AlwaysFiresSampledFault()
    {
        await using var fx = new ChaosPipelineFixture(SingleErrorProfile(503));
        fx.Store.Add(RandomPolicy("test.single-error", intensity: 1.0));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(0, fx.UpstreamCallCount);
        Assert.Equal("boom", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task IntensityZero_NeverFires()
    {
        await using var fx = new ChaosPipelineFixture(SingleErrorProfile());
        fx.Store.Add(RandomPolicy("test.single-error", intensity: 0.0));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Paused_PassesThrough()
    {
        await using var fx = new ChaosPipelineFixture(SingleErrorProfile());
        fx.Store.Add(RandomPolicy("test.single-error", intensity: 1.0));
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ExcludePaths_AreNeverFaulted()
    {
        await using var fx = new ChaosPipelineFixture(SingleErrorProfile());
        fx.Store.Add(RandomPolicy("test.single-error", intensity: 1.0, excludePaths: new[] { "/health" }));

        var excluded = await fx.Client.GetAsync("/health/ready");
        var faulted = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, excluded.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, faulted.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount); // only /health/ready forwarded
    }

    [Fact]
    public async Task MaxFires_CapsTotalFires()
    {
        await using var fx = new ChaosPipelineFixture(SingleErrorProfile());
        fx.Store.Add(RandomPolicy("test.single-error", intensity: 1.0, maxFires: 2));

        var first = await fx.Client.GetAsync("/api/x");
        var second = await fx.Client.GetAsync("/api/x");
        var third = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode); // cap reached -> forwarded
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Matcher_OnlyFaultsMatchingRequests()
    {
        await using var fx = new ChaosPipelineFixture(SingleErrorProfile());
        fx.Store.Add(RandomPolicy(
            "test.single-error",
            intensity: 1.0,
            matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        var match = await fx.Client.GetAsync("/api/v1/foo");
        var noMatch = await fx.Client.GetAsync("/other");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, match.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noMatch.StatusCode);
    }

    [Fact]
    public async Task SameSeed_ProducesIdenticalFaultSequence()
    {
        var first = await CollectStatuses(seed: 4321);
        var second = await CollectStatuses(seed: 4321);

        Assert.Equal(first, second);
        // Sanity: the two-error profile produces a mix, not a constant.
        Assert.True(first.Distinct().Count() > 1, "expected a mix of statuses from the two-entry profile");
    }

    [Fact]
    public async Task DifferentSeeds_ProduceDifferentSequences()
    {
        var a = await CollectStatuses(seed: 1);
        var b = await CollectStatuses(seed: 2);

        Assert.NotEqual(a, b);
    }

    private static async Task<List<int>> CollectStatuses(int seed)
    {
        await using var fx = new ChaosPipelineFixture(TwoErrorProfile());
        fx.Store.Add(RandomPolicy("test.two-error", intensity: 1.0, seed: seed));

        var statuses = new List<int>(30);
        for (var i = 0; i < 30; i++)
        {
            var resp = await fx.Client.GetAsync("/api/x");
            statuses.Add((int)resp.StatusCode);
        }

        return statuses;
    }
}
