// <copyright file="ChaosErrorMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosErrorMiddlewareTests
{
    [Fact]
    public async Task NoPolicy_ForwardsToUpstream()
    {
        await using var fx = new ChaosPipelineFixture();

        var resp = await fx.Client.GetAsync("/api/anything");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
        Assert.Equal("/api/anything", fx.LastUpstreamPath);
    }

    [Fact]
    public async Task ProbabilityOne_AlwaysReturnsConfiguredStatus()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "always-fail",
            Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, "ServerBusy", "text/plain", null, Probability: 1.0, FailFirst: null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var resp = await fx.Client.GetAsync("/api/whatever");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(0, fx.UpstreamCallCount);
        Assert.Equal("ServerBusy", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProbabilityZero_NeverFires()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "never-fail",
            Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, Probability: 0.0, FailFirst: null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var resp = await fx.Client.GetAsync("/api/whatever");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task CustomHeaders_AppliedToResponse()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "throttled",
            Matcher: null,
            Latency: null,
            Error: new ErrorConfig(
                Status: 429,
                Body: null,
                ContentType: null,
                Headers: new Dictionary<string, string>
                {
                    ["x-ms-retry-after-ms"] = "250",
                    ["Retry-After"] = "1",
                },
                Probability: 1.0,
                FailFirst: null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)429, resp.StatusCode);
        Assert.Equal("250", resp.Headers.GetValues("x-ms-retry-after-ms").Single());
        Assert.Equal("1", resp.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Paused_PassesThroughEvenWithAlwaysFirePolicy()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "always-fail",
            Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, Probability: 1.0, FailFirst: null),
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
    public async Task Resume_AfterPause_FaultsResume()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "always-fail",
            Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, Probability: 1.0, FailFirst: null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));
        fx.Store.Pause();
        (await fx.Client.GetAsync("/api/x")).EnsureSuccessStatusCode();
        fx.Reset();

        fx.Store.Resume();
        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(0, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task FireOnce_OverridesZeroProbability_OnlyOnce()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-zero",
            Matcher: null,
            Latency: null,
            // Probability 0 means it would normally never fire.
            Error: new ErrorConfig(418, "fire-once", "text/plain", null, Probability: 0.0, FailFirst: null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));
        fx.Store.SetFireOnce("error");

        var first = await fx.Client.GetAsync("/api/x");
        var second = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)418, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount); // only the 2nd reaches upstream
    }

    [Fact]
    public async Task Matcher_OnlyFiresOnMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "api-error",
            Matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null),
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, Probability: 1.0, FailFirst: null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var matchHit = await fx.Client.GetAsync("/api/v1/foo");
        var noMatchHit = await fx.Client.GetAsync("/other/path");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, matchHit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noMatchHit.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount); // only /other/path forwarded
    }

    [Fact]
    public async Task FailFirst_FiresFirstNRequestsThenForwards()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "first-2",
            Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, Probability: 1.0, FailFirst: 2),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        // Use same request key (path + method) so all three hits compete for the same failFirst bucket.
        var first = await fx.Client.GetAsync("/api/x");
        var second = await fx.Client.GetAsync("/api/x");
        var third = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task FirstInstalledWins_OnMatcherOverlap()
    {
        await using var fx = new ChaosPipelineFixture();
        // Two policies both target the same path; per D12 the FIRST installed wins.
        fx.Store.Add(new ActivePolicy(
            Id: "first-503",
            Matcher: new RequestMatcher(null, "/api", null),
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, 1.0, null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));
        fx.Store.Add(new ActivePolicy(
            Id: "second-418",
            Matcher: new RequestMatcher(null, "/api", null),
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 1.0, null),
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }
}
