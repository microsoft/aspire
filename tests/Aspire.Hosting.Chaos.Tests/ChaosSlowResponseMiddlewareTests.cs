// <copyright file="ChaosSlowResponseMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosSlowResponseMiddlewareTests
{
    private static ActivePolicy Slow(
        string body = "hello-slow",
        int status = 200,
        int bytesPerSecond = 8192, // 8 KB/s - fast enough that tests complete in <1s with small bodies
        int chunkSize = 4,
        double probability = 1.0,
        int? failFirst = null,
        RequestMatcher? matcher = null,
        string id = "slow")
        => new(
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
            SlowResponse: new SlowResponseConfig(
                Status: status,
                ContentType: "text/plain",
                Body: Encoding.UTF8.GetBytes(body),
                BytesPerSecond: bytesPerSecond,
                ChunkSize: chunkSize,
                Probability: probability,
                FailFirst: failFirst),
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
    public async Task ProbabilityOne_DeliversFullBodyAndDoesNotCallUpstream()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Slow(body: "hello-slow", bytesPerSecond: 16384));

        var resp = await fx.Client.GetAsync("/api/x");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello-slow", body);
        Assert.Equal(0, fx.UpstreamCallCount); // upstream never called
    }

    [Fact]
    public async Task ProbabilityZero_NeverFires()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Slow(probability: 0.0));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Paused_PassesThroughEvenWithAlwaysFirePolicy()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Slow());
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task RateActuallyThrottles_LongBodyTakesProportionalTime()
    {
        // 100 bytes at 1000 bytes/sec = ~100ms. Cap the test wait at 2s.
        // Assert lower bound only - upper bound is too flaky on a loaded CI.
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Slow(body: new string('A', 100), bytesPerSecond: 1000, chunkSize: 10));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await fx.Client.GetAsync("/api/x");
        var body = await resp.Content.ReadAsStringAsync();
        sw.Stop();

        Assert.Equal(100, body.Length);
        // At 1000 bytes/sec the response should take at least ~80ms (allow 20ms slack).
        Assert.True(sw.ElapsedMilliseconds >= 80,
            $"Expected slow response to take >= 80ms; actual {sw.ElapsedMilliseconds}ms (rate not enforced?)");
    }

    [Fact]
    public async Task Matcher_OnlyFiresOnMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Slow(matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        var matched = await fx.Client.GetAsync("/api/v1/foo");
        var unmatched = await fx.Client.GetAsync("/other/path");

        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        Assert.Equal("hello-slow", await matched.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, unmatched.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount); // only /other reached upstream
    }

    [Fact]
    public async Task ChaosPath_IsNeverFaulted()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Slow());

        var resp = await fx.Client.GetAsync("/chaos/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
