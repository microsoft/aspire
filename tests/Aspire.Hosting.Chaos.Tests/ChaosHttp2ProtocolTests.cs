// <copyright file="ChaosHttp2ProtocolTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Validates the chaos middleware pipeline operates correctly under HTTP/2 (the
/// transport gRPC unary calls ride on). The middleware operates on
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/> which is protocol-agnostic
/// in principle — these tests pin that invariant so a future Kestrel / YARP
/// change can't silently regress gRPC behavior.
/// </summary>
/// <remarks>
/// TestServer supports unencrypted HTTP/2 (h2c) when the client sets
/// <c>HttpRequestMessage.Version = HttpVersion.Version20</c> and
/// <c>VersionPolicy = HttpVersionPolicy.RequestVersionExact</c>.
/// gRPC unary is exactly an HTTP/2 POST with <c>application/grpc</c>
/// content-type and a length-prefixed protobuf body — if HTTP/2 + arbitrary
/// content-types round-trip through our pipeline with chaos applying, gRPC
/// unary works.
/// </remarks>
public class ChaosHttp2ProtocolTests
{
    [Fact]
    public async Task ErrorMiddleware_Fires_OverHttp2()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-grpc-err", Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        var response = await SendHttp2(fx.Client, HttpMethod.Post, "/grpc/SomeService/SomeMethod", "application/grpc");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, fx.UpstreamCallCount);
        Assert.Equal(1, fx.Store.GetFireCounts("p-grpc-err")["error"]);
    }

    [Fact]
    public async Task LatencyMiddleware_Fires_OverHttp2()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-grpc-lat", Matcher: null,
            Latency: new LatencyConfig(20, 30, 1.0, null),
            Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await SendHttp2(fx.Client, HttpMethod.Post, "/grpc/SomeService/SomeMethod", "application/grpc");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 15, $"expected >=15ms (some latency fired) but got {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, fx.Store.GetFireCounts("p-grpc-lat")["latency"]);
    }

    [Fact]
    public async Task HeaderTamperMiddleware_Fires_OverHttp2()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-grpc-ht", Matcher: null,
            Latency: null, Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: new HeaderTamperConfig(
                Direction: HeaderTamperDirection.Request,
                Remove: new[] { "Authorization" },
                Set: new Dictionary<string, string> { ["x-chaos-injected"] = "true" },
                Add: null),
            PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        var response = await SendHttp2(fx.Client, HttpMethod.Post, "/grpc/SomeService/SomeMethod", "application/grpc", req =>
        {
            req.Headers.Add("Authorization", "Bearer should-be-stripped");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fx.LastUpstreamRequestHeaders);
        Assert.False(fx.LastUpstreamRequestHeaders!.ContainsKey("Authorization"), "Authorization should have been removed by tamper");
        Assert.Equal("true", fx.LastUpstreamRequestHeaders["x-chaos-injected"][0]);
    }

    [Fact]
    public async Task MatcherWithPathPrefix_MatchesGrpcPath()
    {
        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-grpc-scoped", Matcher: new RequestMatcher(Method: null, PathPrefix: "/grpc/", PathContains: null, HeaderEquals: null, HeaderContains: null),
            Latency: null,
            Error: new ErrorConfig(429, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        var grpcResponse = await SendHttp2(fx.Client, HttpMethod.Post, "/grpc/Foo/Bar", "application/grpc");
        var restResponse = await SendHttp2(fx.Client, HttpMethod.Get, "/api/things", "application/json");

        Assert.Equal(HttpStatusCode.TooManyRequests, grpcResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, restResponse.StatusCode);
    }

    [Fact]
    public async Task RequestRoundTripsCleanly_OverHttp2_WhenNoPolicyInstalled()
    {
        await using var fx = new ChaosPipelineFixture();

        var response = await SendHttp2(fx.Client, HttpMethod.Post, "/grpc/SomeService/SomeMethod", "application/grpc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fx.UpstreamCallCount);
        Assert.Equal("/grpc/SomeService/SomeMethod", fx.LastUpstreamPath);
    }

    /// <summary>
    /// Issues a request to the TestServer's HttpClient using HTTP/2 (h2c).
    /// Mirrors the wire shape gRPC.Net.Client uses for unary calls: HTTP/2 POST
    /// with <c>application/grpc</c> content-type and a small binary body.
    /// </summary>
    private static async Task<HttpResponseMessage> SendHttp2(
        HttpClient client,
        HttpMethod method,
        string path,
        string contentType,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (method == HttpMethod.Post)
        {
            // Minimal length-prefixed payload mirroring gRPC's framing (5-byte prefix + body).
            request.Content = new ByteArrayContent(new byte[] { 0, 0, 0, 0, 0 });
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        configureRequest?.Invoke(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
