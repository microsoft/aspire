// <copyright file="ChaosPathSkipTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// All three middlewares early-return for paths starting with <c>/chaos</c>. This is
/// defense in depth so dashboard healthz probes and the policy management API never
/// get faulted - even when an "always-fire" policy is installed.
/// </summary>
public class ChaosPathSkipTests
{
    [Fact]
    public async Task ChaosPath_IsNeverErrored_EvenWithAlwaysFirePolicy()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "global-fail",
            Matcher: null,
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

        var resp = await fx.Client.GetAsync("/chaos/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ChaosPolicies_IsNeverErrored()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "global-fail",
            Matcher: null,
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

        var resp = await fx.Client.GetAsync("/chaos/policies");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ChaosPath_PausedFlagDoesNotMatter()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "global-fail",
            Matcher: null,
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
        // Even with no pause, /chaos/* must pass through.
        Assert.False(fx.Store.IsPaused);

        var resp = await fx.Client.GetAsync("/chaos/state");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }
}
