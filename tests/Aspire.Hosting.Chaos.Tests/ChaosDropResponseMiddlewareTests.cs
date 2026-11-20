// <copyright file="ChaosDropResponseMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosDropResponseMiddlewareTests
{
    private static ActivePolicy Drop(double probability = 1.0, int? failFirst = null, RequestMatcher? matcher = null, string id = "drop")
        => new(
            Id: id,
            Matcher: matcher,
            Latency: null,
            Error: null,
            ReplayDuplicate: null,
            DropResponse: new DropResponseConfig(probability, failFirst),
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null);

    [Fact]
    public async Task NoDropPolicy_ForwardsToUpstream()
    {
        await using var fx = new ChaosPipelineFixture();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    /// <summary>
    /// Sends a request and asserts it does NOT complete within the timeout window -
    /// proves the proxy is hanging the request (drop semantics) without depending on
    /// TestHost's cancellation-token propagation behavior.
    /// </summary>
    private static async Task AssertRequestHangs(HttpClient client, string path, int hangsForMs = 200)
    {
        // HttpClient.GetAsync with HttpCompletionOption.ResponseHeadersRead returns as soon
        // as headers arrive. Drop never writes headers, so this Task should be waiting
        // forever. We give it `hangsForMs` ms then fail if it completed (would indicate
        // the middleware didn't actually drop).
        var requestTask = client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        var delayTask = Task.Delay(hangsForMs);
        var winner = await Task.WhenAny(requestTask, delayTask);
        Assert.Same(delayTask, winner);
    }

    [Fact]
    public async Task ProbabilityOne_HangsTheRequestAndNeverCallsUpstream()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Drop(probability: 1.0));

        await AssertRequestHangs(fx.Client, "/api/x");

        Assert.Equal(0, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ProbabilityZero_NeverDrops()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Drop(probability: 0.0));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Paused_PassesThroughEvenWithAlwaysDropPolicy()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Drop(probability: 1.0));
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Matcher_OnlyDropsMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Drop(
            probability: 1.0,
            matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        var unmatched = await fx.Client.GetAsync("/other/path");
        Assert.Equal(HttpStatusCode.OK, unmatched.StatusCode);

        await AssertRequestHangs(fx.Client, "/api/v1/foo");

        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ChaosPath_IsNeverDropped_EvenWithAlwaysFirePolicy()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Drop(probability: 1.0));

        var resp = await fx.Client.GetAsync("/chaos/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task MaxFires_StopsDroppingAfterGlobalCap_EvenWithDifferentRequestKeys()
    {
        // MaxFires:1 + FailFirst:1 + multiple request keys. FailFirst alone would
        // fire per-key (so 3 different paths = 3 fires); MaxFires caps the global
        // total at 1. This is the DTFx Azure Queue Storage scenario: multiple
        // partitions = multiple request keys; we want exactly N drops across all
        // partitions, not N per partition.
        await using var fx = new ChaosPipelineFixture();
        var policy = new ActivePolicy(
            Id: "max1",
            Matcher: null,
            Latency: null, Error: null, ReplayDuplicate: null,
            DropResponse: new DropResponseConfig(Probability: 1.0, FailFirst: 1, MaxFires: 1),
            RateLimit: null, HeaderTamper: null, PartialResponse: null,
            IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null);
        fx.Store.Add(policy);

        // First request — different path = different request key — fires once (1/1)
        await AssertRequestHangs(fx.Client, "/api/v1/foo");

        // Second request, different path = different request key — would fire under
        // FailFirst alone, but MaxFires caps at 1 so this passes through to upstream.
        var resp2 = await fx.Client.GetAsync("/api/v2/bar");
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        // Third request, yet another path — same story.
        var resp3 = await fx.Client.GetAsync("/api/v3/baz");
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

        Assert.Equal(2, fx.UpstreamCallCount); // resp2 and resp3 reached upstream
    }
}
