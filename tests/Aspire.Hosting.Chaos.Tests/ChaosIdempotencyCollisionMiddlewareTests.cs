// <copyright file="ChaosIdempotencyCollisionMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosIdempotencyCollisionMiddlewareTests
{
    private static ActivePolicy IdempotencyCollision(
        int windowMs = 60_000,
        int status = 409,
        string? body = null,
        string keyHeaderName = "Idempotency-Key",
        RequestMatcher? matcher = null,
        string id = "ic")
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
            IdempotencyCollision: new IdempotencyCollisionConfig(
                KeyHeaderName: keyHeaderName,
                Status: status,
                Body: body,
                ContentType: body is null ? null : "text/plain",
                Headers: null,
                WindowMs: windowMs),
            SlowResponse: null,
            ExpiresAt: null);

    private static HttpRequestMessage Get(string path, string? idempotencyKey = null, string keyHeader = "Idempotency-Key")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (idempotencyKey is not null)
        {
            req.Headers.Add(keyHeader, idempotencyKey);
        }
        return req;
    }

    [Fact]
    public async Task NoPolicy_ForwardsToUpstream()
    {
        await using var fx = new ChaosPipelineFixture();

        var resp = await fx.Client.SendAsync(Get("/api/x", "abc"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task NoKeyHeader_AlwaysForwards()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision());

        var resp1 = await fx.Client.SendAsync(Get("/api/x"));
        var resp2 = await fx.Client.SendAsync(Get("/api/x"));
        var resp3 = await fx.Client.SendAsync(Get("/api/x"));

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
        Assert.Equal(3, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task FirstSighting_Forwards()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision());

        var resp = await fx.Client.SendAsync(Get("/api/x", "abc"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task SecondSightingSameKey_Collides()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision(status: 409, body: "duplicate"));

        var first = await fx.Client.SendAsync(Get("/api/x", "abc"));
        var second = await fx.Client.SendAsync(Get("/api/x", "abc"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)409, second.StatusCode);
        Assert.Equal("duplicate", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, fx.UpstreamCallCount); // only the first forwarded
    }

    [Fact]
    public async Task DifferentKeys_BothForward()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision());

        var first = await fx.Client.SendAsync(Get("/api/x", "key-a"));
        var second = await fx.Client.SendAsync(Get("/api/x", "key-b"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task AfterWindowExpires_KeyCanBeReused()
    {
        await using var fx = new ChaosPipelineFixture();

        // Use a window comfortably larger than the round-trip of the first two requests so the
        // "blocked" assertion is robust under CI load, then wait well past the window for reuse.
        fx.Store.Add(IdempotencyCollision(windowMs: 1_000));

        (await fx.Client.SendAsync(Get("/api/x", "abc"))).EnsureSuccessStatusCode();
        var blocked = await fx.Client.SendAsync(Get("/api/x", "abc"));
        Assert.Equal((HttpStatusCode)409, blocked.StatusCode);

        await Task.Delay(1_300);

        var reused = await fx.Client.SendAsync(Get("/api/x", "abc"));
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
    }

    [Fact]
    public async Task CustomKeyHeader_Honored()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision(keyHeaderName: "X-Request-ID"));

        var first = await fx.Client.SendAsync(Get("/api/x", "abc", "X-Request-ID"));
        var second = await fx.Client.SendAsync(Get("/api/x", "abc", "X-Request-ID"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)409, second.StatusCode);
    }

    [Fact]
    public async Task CustomKeyHeader_DefaultHeaderIgnored()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision(keyHeaderName: "X-Request-ID"));

        // Sending the default Idempotency-Key when policy expects X-Request-ID
        // should NOT trigger collision tracking.
        var first = await fx.Client.SendAsync(Get("/api/x", "abc", "Idempotency-Key"));
        var second = await fx.Client.SendAsync(Get("/api/x", "abc", "Idempotency-Key"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Paused_PassesThroughCollision()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision());

        // First request fills the dedupe cache.
        await fx.Client.SendAsync(Get("/api/x", "abc"));
        fx.Store.Pause();

        // Second request would normally collide; with pause it forwards.
        var resp = await fx.Client.SendAsync(Get("/api/x", "abc"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Matcher_OnlyTracksMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision(matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        await fx.Client.SendAsync(Get("/api/v1/foo", "abc"));
        var matchCollide = await fx.Client.SendAsync(Get("/api/v1/foo", "abc"));
        Assert.Equal((HttpStatusCode)409, matchCollide.StatusCode);

        // Same key on an unmatched path should NOT collide.
        var unmatched1 = await fx.Client.SendAsync(Get("/other", "abc"));
        var unmatched2 = await fx.Client.SendAsync(Get("/other", "abc"));
        Assert.Equal(HttpStatusCode.OK, unmatched1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unmatched2.StatusCode);
    }

    [Fact]
    public async Task ChaosPath_IsNeverChecked()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(IdempotencyCollision());

        // Even hitting /chaos/* twice with the same key shouldn't trigger collision.
        var first = await fx.Client.SendAsync(Get("/chaos/healthz", "abc"));
        var second = await fx.Client.SendAsync(Get("/chaos/healthz", "abc"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
