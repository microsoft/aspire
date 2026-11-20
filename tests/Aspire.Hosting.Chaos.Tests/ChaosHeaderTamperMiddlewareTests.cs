// <copyright file="ChaosHeaderTamperMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosHeaderTamperMiddlewareTests
{
    private static ActivePolicy Tamper(
        HeaderTamperDirection direction,
        IReadOnlyList<string>? remove = null,
        IReadOnlyDictionary<string, string>? set = null,
        IReadOnlyDictionary<string, string>? add = null,
        RequestMatcher? matcher = null,
        string id = "ht")
        => new(
            Id: id,
            Matcher: matcher,
            Latency: null,
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: new HeaderTamperConfig(direction, remove, set, add),
            PartialResponse: null,
            IdempotencyCollision: null,
            SlowResponse: null,
            ExpiresAt: null);

    [Fact]
    public async Task NoPolicy_RequestHeadersFlowThroughUnchanged()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Client.DefaultRequestHeaders.Add("X-Original", "value");

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("value", fx.LastUpstreamRequestHeaders!["X-Original"].Single());
    }

    [Fact]
    public async Task Request_Set_OverridesIncomingHeader()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Request,
            set: new Dictionary<string, string> { ["X-Trace"] = "tampered" }));
        fx.Client.DefaultRequestHeaders.Add("X-Trace", "original");

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("tampered", fx.LastUpstreamRequestHeaders!["X-Trace"].Single());
    }

    [Fact]
    public async Task Request_Set_AddsHeaderWhenAbsent()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Request,
            set: new Dictionary<string, string> { ["X-Injected"] = "yes" }));

        await fx.Client.GetAsync("/api/x");

        Assert.Equal("yes", fx.LastUpstreamRequestHeaders!["X-Injected"].Single());
    }

    [Fact]
    public async Task Request_Remove_StripsHeader()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Request,
            remove: new[] { "Authorization" }));
        fx.Client.DefaultRequestHeaders.Add("Authorization", "Bearer secret");

        await fx.Client.GetAsync("/api/x");

        Assert.False(fx.LastUpstreamRequestHeaders!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Request_Add_AppendsSecondValue()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Request,
            add: new Dictionary<string, string> { ["X-Multi"] = "second" }));
        fx.Client.DefaultRequestHeaders.Add("X-Multi", "first");

        await fx.Client.GetAsync("/api/x");

        var values = fx.LastUpstreamRequestHeaders!["X-Multi"];
        Assert.Equal(2, values.Length);
        Assert.Contains("first", values);
        Assert.Contains("second", values);
    }

    [Fact]
    public async Task Request_RemoveThenSet_Wins()
    {
        // Order: Remove, then Set, then Add. Removing and setting the same header
        // should leave the Set value behind.
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Request,
            remove: new[] { "X-Foo" },
            set: new Dictionary<string, string> { ["X-Foo"] = "set-value" }));
        fx.Client.DefaultRequestHeaders.Add("X-Foo", "incoming");

        await fx.Client.GetAsync("/api/x");

        Assert.Equal("set-value", fx.LastUpstreamRequestHeaders!["X-Foo"].Single());
    }

    [Fact]
    public async Task Response_Set_AppearsOnResponseHeaders()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Response,
            set: new Dictionary<string, string> { ["X-Tampered"] = "yes" }));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("yes", resp.Headers.GetValues("X-Tampered").Single());
    }

    [Fact]
    public async Task Response_Remove_StripsUpstreamHeader()
    {
        await using var fx = new ChaosPipelineFixture();
        // Terminal handler always sends X-Upstream-Hit; remove it via tamper.
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Response,
            remove: new[] { "X-Upstream-Hit" }));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("X-Upstream-Hit"));
    }

    [Fact]
    public async Task Both_AppliesToRequestAndResponse()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Both,
            set: new Dictionary<string, string> { ["X-Both"] = "both" }));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal("both", fx.LastUpstreamRequestHeaders!["X-Both"].Single());
        Assert.Equal("both", resp.Headers.GetValues("X-Both").Single());
    }

    [Fact]
    public async Task Matcher_OnlyTampersMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Request,
            set: new Dictionary<string, string> { ["X-Tag"] = "scoped" },
            matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        await fx.Client.GetAsync("/api/v1/foo");
        Assert.Equal("scoped", fx.LastUpstreamRequestHeaders!["X-Tag"].Single());

        fx.Reset();
        await fx.Client.GetAsync("/other/path");
        Assert.False(fx.LastUpstreamRequestHeaders!.ContainsKey("X-Tag"));
    }

    [Fact]
    public async Task Paused_PassesThroughEvenWithTamper()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Both,
            set: new Dictionary<string, string> { ["X-Tampered"] = "yes" }));
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.False(resp.Headers.Contains("X-Tampered"));
        Assert.False(fx.LastUpstreamRequestHeaders!.ContainsKey("X-Tampered"));
    }

    [Fact]
    public async Task ChaosPath_IsNeverTampered()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Tamper(
            HeaderTamperDirection.Both,
            set: new Dictionary<string, string> { ["X-Tampered"] = "yes" }));

        var resp = await fx.Client.GetAsync("/chaos/healthz");

        Assert.False(resp.Headers.Contains("X-Tampered"));
    }
}
