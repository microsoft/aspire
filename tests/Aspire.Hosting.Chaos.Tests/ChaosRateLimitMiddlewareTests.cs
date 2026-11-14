// <copyright file="ChaosRateLimitMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosRateLimitMiddlewareTests
{
    private static ActivePolicy RateLimit(
        int requestsPerWindow,
        int windowMs,
        int status = 429,
        IReadOnlyDictionary<string, string>? headers = null,
        RequestMatcher? matcher = null,
        string id = "rl")
        => new(
            Id: id,
            Matcher: matcher,
            Latency: null,
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: new RateLimitConfig(requestsPerWindow, windowMs, status, headers),
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null);

    [Fact]
    public async Task NoPolicy_ForwardsToUpstream()
    {
        await using var fx = new ChaosPipelineFixture();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task WithinBudget_AllForwarded()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(requestsPerWindow: 3, windowMs: 10_000));

        for (var i = 0; i < 3; i++)
        {
            var resp = await fx.Client.GetAsync("/api/x");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        Assert.Equal(3, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ExceedsBudget_ReturnsConfiguredStatus()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(requestsPerWindow: 2, windowMs: 10_000, status: 429));

        (await fx.Client.GetAsync("/api/x")).EnsureSuccessStatusCode();
        (await fx.Client.GetAsync("/api/x")).EnsureSuccessStatusCode();
        var blocked = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Equal(2, fx.UpstreamCallCount); // 3rd request didn't reach upstream
    }

    [Fact]
    public async Task ExceedsBudget_HeadersAppliedToBlockedResponse()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(
            requestsPerWindow: 1,
            windowMs: 10_000,
            status: 429,
            headers: new Dictionary<string, string> { ["Retry-After"] = "5" }));

        (await fx.Client.GetAsync("/api/x")).EnsureSuccessStatusCode();
        var blocked = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Equal("5", blocked.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Paused_PassesThroughEvenOverBudget()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(requestsPerWindow: 0, windowMs: 10_000));
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Matcher_OnlyRateLimitsMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(
            requestsPerWindow: 1,
            windowMs: 10_000,
            matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        // Burn the budget on the matching path.
        (await fx.Client.GetAsync("/api/v1/foo")).EnsureSuccessStatusCode();
        var matchedBlocked = await fx.Client.GetAsync("/api/v1/foo");
        Assert.Equal((HttpStatusCode)429, matchedBlocked.StatusCode);

        // Non-matching path always forwards regardless of how many times we hit it.
        for (var i = 0; i < 3; i++)
        {
            var unmatched = await fx.Client.GetAsync("/other/path");
            Assert.Equal(HttpStatusCode.OK, unmatched.StatusCode);
        }
    }

    [Fact]
    public async Task FireOnce_BlocksNextRequestImmediately()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(requestsPerWindow: 1_000_000, windowMs: 10_000)); // effectively no limit
        fx.Store.SetFireOnce("rate-limit");

        var blocked = await fx.Client.GetAsync("/api/x");
        var passes = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, passes.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ChaosPath_IsNeverRateLimited()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(RateLimit(requestsPerWindow: 0, windowMs: 10_000));

        var resp = await fx.Client.GetAsync("/chaos/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
