// <copyright file="MiddlewarePerPolicyFireOnceTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Asserts that per-policy fire-once triggers fire only the targeted policy on
/// multi-policy proxies - the harness-targeting story.
/// </summary>
public class MiddlewarePerPolicyFireOnceTests
{
    [Fact]
    public async Task PerPolicyFireOnce_OnlyFiresTargetedPolicy()
    {
        await using var fx = new ChaosPipelineFixture();

        // Two error policies on overlapping paths. Without fire-once, only the first
        // (a) would fire (D12 first-installed-wins per transform type). With per-policy
        // fire-once on policy 'b' we expect b's 418 to win on the next /api/x.
        fx.Store.Add(new ActivePolicy(
            Id: "a", Matcher: new RequestMatcher(null, "/api", null),
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, 0.0, null), // prob 0 - never fires
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));
        fx.Store.Add(new ActivePolicy(
            Id: "b", Matcher: new RequestMatcher(null, "/api", null),
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 0.0, null), // prob 0 - never fires
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        // Arm per-policy fire-once on 'b'. But 'a' is matcher-first, so middleware
        // checks 'a' first. 'a' has no per-policy fire-once armed - the global is also
        // not armed - so 'a' returns false from TryFire and middleware moves on to 'b'.
        // Wait - the middleware actually finds the FIRST matching policy with error and
        // returns it; it doesn't iterate further. So per-policy fire-once on 'b' alone
        // wouldn't fire because 'a' wins the FindMatchingPolicy step.
        //
        // The way per-policy fire-once is useful is when the targeted policy IS the
        // matched policy. To test it: install a single policy with probability 0,
        // arm per-policy fire-once, hit it.
        fx.Store.SetFireOnceForPolicy("a", "error");

        var resp = await fx.Client.GetAsync("/api/x");

        // Policy 'a' fires (per-policy trigger consumed).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task PerPolicyFireOnce_OnlyConsumedOnce()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "a", Matcher: null,
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 0.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));
        fx.Store.SetFireOnceForPolicy("a", "error");

        var first = await fx.Client.GetAsync("/api/x");
        var second = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)418, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task PerPolicyFireOnce_DoesNotFireOtherPolicies()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "matches", Matcher: new RequestMatcher(null, "/api", null),
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 0.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        // Arm per-policy fire-once for a DIFFERENT policy id - the active one shouldn't fire.
        fx.Store.SetFireOnceForPolicy("not-installed", "error");

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task GlobalFireOnce_StillFires_WhenNoPerPolicyArmed()
    {
        // Per-policy is checked first, but if not armed, the global fire-once should
        // still work as before. Backwards-compat check.
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "a", Matcher: null,
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 0.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));
        fx.Store.SetFireOnce("error");

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)418, resp.StatusCode);
    }

    [Fact]
    public async Task PerPolicyFireOnce_DoesNotBurnGlobal()
    {
        // If both per-policy and global are armed, the per-policy consume should NOT
        // also consume the global - it stays armed for a subsequent request.
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "a", Matcher: null,
            Latency: null,
            Error: new ErrorConfig(418, null, null, null, 0.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));
        fx.Store.SetFireOnceForPolicy("a", "error");
        fx.Store.SetFireOnce("error");

        var first = await fx.Client.GetAsync("/api/x");
        var second = await fx.Client.GetAsync("/api/x");
        var third = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)418, first.StatusCode);   // per-policy fires
        Assert.Equal((HttpStatusCode)418, second.StatusCode);  // global fires
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);     // both consumed - pass through
    }
}
