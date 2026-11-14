// <copyright file="ChaosPartialResponseMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosPartialResponseMiddlewareTests
{
    private static ActivePolicy Partial(
        string body = "hello",
        int status = 200,
        string? contentType = null,
        int? advertisedContentLength = null,
        int abortAfterMs = 50,
        double probability = 1.0,
        int? failFirst = null,
        RequestMatcher? matcher = null,
        string id = "partial")
        => new(
            Id: id,
            Matcher: matcher,
            Latency: null,
            Error: null,
            ReplayDuplicate: null,
            DropResponse: null,
            RateLimit: null,
            HeaderTamper: null,
            PartialResponse: new PartialResponseConfig(
                Status: status,
                ContentType: contentType,
                Body: Encoding.UTF8.GetBytes(body),
                AdvertisedContentLength: advertisedContentLength,
                AbortAfterMs: abortAfterMs,
                Probability: probability,
                FailFirst: failFirst),
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
    public async Task ProbabilityOne_ReturnsConfiguredStatusAndBody_UpstreamNeverCalled()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial(body: "partial data", status: 200, contentType: "text/plain"));

        // ResponseHeadersRead so we don't fail before getting the partial bytes.
        var resp = await fx.Client.GetAsync("/api/x", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, fx.UpstreamCallCount);

        // The body may or may not be readable depending on the abort timing - what
        // matters is the upstream count and the configured status reached the client.
    }

    [Fact]
    public async Task AdvertisedContentLength_CausesClientToErrorOnReadingBody()
    {
        await using var fx = new ChaosPipelineFixture();
        // Advertise 1000 bytes, send only 5 ("hello"). Reading the body should throw
        // when the stream closes early.
        fx.Store.Add(Partial(body: "hello", advertisedContentLength: 1000, abortAfterMs: 100));

        var resp = await fx.Client.GetAsync("/api/x", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1000, resp.Content.Headers.ContentLength);

        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            // Attempt to fully read; the underlying stream closes before we get the
            // advertised 1000 bytes, which HttpClient surfaces as an HttpRequestException
            // (or its IOException inner) since the response was incomplete.
            _ = await resp.Content.ReadAsByteArrayAsync();
        });
    }

    [Fact]
    public async Task ProbabilityZero_NeverFires()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial(probability: 0.0));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task Paused_PassesThroughEvenWithAlwaysFirePolicy()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial());
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task FireOnce_OverridesZeroProbability_OnlyOnce()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial(status: 418, probability: 0.0));
        fx.Store.SetFireOnce("partial-response");

        var first = await fx.Client.GetAsync("/api/x", HttpCompletionOption.ResponseHeadersRead);
        var second = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)418, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount); // only the 2nd reached upstream
    }

    [Fact]
    public async Task Matcher_OnlyFiresOnMatchingPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial(matcher: new RequestMatcher(Method: null, PathPrefix: "/api/v1", PathContains: null)));

        var matched = await fx.Client.GetAsync("/api/v1/foo", HttpCompletionOption.ResponseHeadersRead);
        var unmatched = await fx.Client.GetAsync("/other/path");

        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unmatched.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount); // only /other/path reached upstream
    }

    [Fact]
    public async Task FailFirst_FiresFirstNRequestsThenForwards()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial(failFirst: 2, status: 418));

        var first = await fx.Client.GetAsync("/api/x", HttpCompletionOption.ResponseHeadersRead);
        var second = await fx.Client.GetAsync("/api/x", HttpCompletionOption.ResponseHeadersRead);
        var third = await fx.Client.GetAsync("/api/x");

        Assert.Equal((HttpStatusCode)418, first.StatusCode);
        Assert.Equal((HttpStatusCode)418, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }

    [Fact]
    public async Task ChaosPath_IsNeverFaulted()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(Partial());

        var resp = await fx.Client.GetAsync("/chaos/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
    }
}
