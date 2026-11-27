// <copyright file="ChaosLatencyMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosLatencyMiddlewareTests
{
    [Fact]
    public async Task NoPolicy_ForwardsImmediately()
    {
        await using var fx = new ChaosPipelineFixture();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ProbabilityOne_StillForwards()
    {
        // Latency must NOT short-circuit - it only delays, then forwards. Validates
        // pipeline ordering: latency runs first, but always continues to the next stage.
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "always-slow",
            Matcher: null,
            Latency: new LatencyConfig(MinMs: 1, MaxMs: 2, Probability: 1.0, FailFirst: null),
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ZeroProbability_DoesNotDelay()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "never-slow",
            Matcher: null,
            Latency: new LatencyConfig(1, 2, Probability: 0.0, FailFirst: null),
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Paused_PassesThroughWithNoDelay()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "always-slow",
            Matcher: null,
            Latency: new LatencyConfig(1, 2, 1.0, null),
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Matcher_OnlyDelaysMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "slow-api",
            Matcher: new RequestMatcher(null, "/api", null),
            Latency: new LatencyConfig(1, 2, 1.0, null),
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var matched = await fx.Client.GetAsync("/api/x");
        var unmatched = await fx.Client.GetAsync("/other");

        // Both should reach upstream - latency never short-circuits.
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unmatched.StatusCode);
        Assert.Equal(2, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task FireOnce_ConsumedByLatency_OnlyOnce()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-zero-slow",
            Matcher: null,
            Latency: new LatencyConfig(1, 2, Probability: 0.0, FailFirst: null),
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));
        fx.Store.SetFireOnce("latency");

        // Both requests should forward to upstream - latency never short-circuits.
        await fx.Client.GetAsync("/a");
        await fx.Client.GetAsync("/b");

        Assert.Equal(2, fx.UpstreamCallCount);
        // The fire-once was consumed by the first request. There's no asserted side
        // effect we can observe here without timing (which would flake), so we
        // additionally verify it's no longer armed after the first hit:
        Assert.False(fx.Store.ConsumeFireOnce("latency"));
    }
}
