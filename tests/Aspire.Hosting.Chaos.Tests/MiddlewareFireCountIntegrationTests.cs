// <copyright file="MiddlewareFireCountIntegrationTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Asserts that each firing middleware increments the policy's fire counter exactly
/// once per fire, with the right transform key. These counters are exposed via
/// GET /chaos/policies for harness assertions ("did the chaos actually happen?").
/// </summary>
public class MiddlewareFireCountIntegrationTests
{
    [Fact]
    public async Task ErrorMiddleware_Fires_IncrementsErrorCounter()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p", Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");

        var counts = fx.Store.GetFireCounts("p");
        Assert.Equal(3, counts["error"]);
    }

    [Fact]
    public async Task LatencyMiddleware_Fires_IncrementsLatencyCounter()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p", Matcher: null,
            Latency: new LatencyConfig(1, 2, 1.0, null),
            Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");

        Assert.Equal(2, fx.Store.GetFireCounts("p")["latency"]);
    }

    [Fact]
    public async Task RateLimitMiddleware_OnlyCountsBlockedRequests()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p", Matcher: null,
            Latency: null, Error: null, ReplayDuplicate: null, DropResponse: null,
            RateLimit: new RateLimitConfig(2, 10_000, 429, null),
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        // First 2 admitted, next 2 blocked.
        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");

        var counts = fx.Store.GetFireCounts("p");
        // RecordFire is only called on the blocking path (admit=false).
        Assert.Equal(2, counts.GetValueOrDefault("rate-limit"));
    }

    [Fact]
    public async Task IdempotencyCollisionMiddleware_OnlyCountsCollisions()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p", Matcher: null,
            Latency: null, Error: null, ReplayDuplicate: null, DropResponse: null,
            RateLimit: null, HeaderTamper: null, PartialResponse: null,
            IdempotencyCollision: new IdempotencyCollisionConfig("Idempotency-Key", 409, null, null, null, 60_000),
            SlowResponse: null,
            ExpiresAt: null));

        var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        req1.Headers.Add("Idempotency-Key", "abc");
        await fx.Client.SendAsync(req1);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        req2.Headers.Add("Idempotency-Key", "abc");
        await fx.Client.SendAsync(req2);

        var req3 = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        req3.Headers.Add("Idempotency-Key", "abc");
        await fx.Client.SendAsync(req3);

        // First sighting forwards (no counter); 2nd + 3rd collide (counter += 2).
        Assert.Equal(2, fx.Store.GetFireCounts("p")["idempotency-collision"]);
    }

    [Fact]
    public async Task FireCountsScopedPerPolicy_TwoErrorPoliciesTrackedSeparately()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "a", Matcher: new RequestMatcher(null, "/api/a", null),
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));
        fx.Store.Add(new ActivePolicy(
            Id: "b", Matcher: new RequestMatcher(null, "/api/b", null),
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/a");
        await fx.Client.GetAsync("/api/a");
        await fx.Client.GetAsync("/api/b");

        Assert.Equal(2, fx.Store.GetFireCounts("a")["error"]);
        Assert.Equal(1, fx.Store.GetFireCounts("b")["error"]);
    }
}
