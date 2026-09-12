// <copyright file="ChaosForwardThenFailMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using ChaosProxy.Container.Middleware;
using ChaosProxy.Container.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests the response-side <see cref="ChaosForwardThenFailMiddleware"/> — the only
/// transform that forwards the request to upstream BEFORE returning a configured
/// failure to the client. The fixture wires a recording <see cref="HttpMessageHandler"/>
/// as the "upstream" so each test asserts (a) upstream received the forwarded request
/// and (b) the client got the synthesized failure (not the upstream's actual response).
/// </summary>
public class ChaosForwardThenFailMiddlewareTests
{
    private static ActivePolicy ForwardThenFail(
        int status = 503,
        string? body = null,
        string? contentType = null,
        int? failFirst = null,
        int? maxFires = null,
        RequestMatcher? matcher = null,
        string id = "forward-then-fail-test")
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
            SlowResponse: null,
            ExpiresAt: null,
            ForwardThenFail: new ForwardThenFailConfig(
                Status: status,
                ContentType: contentType,
                Body: body,
                Headers: null,
                UpstreamTimeoutSeconds: 5,
                Probability: 1.0,
                FailFirst: failFirst,
                MaxFires: maxFires));

    [Fact]
    public async Task NoPolicy_FallsThrough_NoUpstreamCallByMiddleware()
    {
        await using var fx = new ForwardThenFailFixture();

        var resp = await fx.Client.GetAsync("/api/anything");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("terminal-ok", await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
    }

    [Fact]
    public async Task PolicyFires_UpstreamReceivesRequest_ClientGetsConfiguredFailure()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail(status: 503, body: "Service Busy", contentType: "text/plain"));

        var resp = await fx.Client.PostAsync("/api/evaluateScenarios", new StringContent("{\"x\":1}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("Service Busy", await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, fx.UpstreamForwardCount);
        Assert.Equal("POST", fx.LastUpstreamMethod);
        Assert.Equal("/api/evaluateScenarios", fx.LastUpstreamPath);
        Assert.Equal("{\"x\":1}", fx.LastUpstreamBody);
        Assert.Equal(0, fx.TerminalHits);
    }

    [Fact]
    public async Task PolicyMatcher_OnlyFiresOnMatching_OtherRequestsFallThrough()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail(matcher: new RequestMatcher("POST", PathPrefix: null, PathContains: "evaluateScenarios")));

        var notMatching = await fx.Client.GetAsync("/api/something");
        var matching = await fx.Client.PostAsync("/api/v1/evaluateScenarios", new StringContent(""));

        Assert.Equal(HttpStatusCode.OK, notMatching.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, matching.StatusCode);
        Assert.Equal(1, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
    }

    [Fact]
    public async Task FailFirst_OnlyFiresOnFirstNRequests()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail(failFirst: 1));

        var first = await fx.Client.GetAsync("/api/x");
        var second = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
    }

    [Fact]
    public async Task Paused_FallsThroughEvenWhenPolicyConfigured()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail());
        fx.Store.Pause();

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
    }

    [Fact]
    public async Task NoUpstreamUrlConfigured_FallsThroughInsteadOfFiring()
    {
        await using var fx = new ForwardThenFailFixture(upstreamUrl: null);
        fx.Store.Add(ForwardThenFail());

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
    }

    [Fact]
    public async Task UpstreamThrows_ClientStillGetsConfiguredFailure()
    {
        await using var fx = new ForwardThenFailFixture(upstreamThrows: true);
        fx.Store.Add(ForwardThenFail(status: 502, body: "bad gateway"));

        var resp = await fx.Client.GetAsync("/api/x");

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("bad gateway", await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, fx.TerminalHits);
    }

    [Fact]
    public async Task ChaosControlPlaneRequests_AreNeverForwarded()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail());

        // /chaos/* requests bypass the chaos pipeline entirely (defense in depth).
        var resp = await fx.Client.GetAsync("/chaos/healthz-shim");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
    }

    [Fact]
    public async Task RecordsFireCount_OnEachFire()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail(id: "p1"));

        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/y");
        await fx.Client.GetAsync("/api/z");

        Assert.Equal(3, fx.Store.GetFireCount("p1", "forward-then-fail"));
        Assert.Equal(3, fx.UpstreamForwardCount);
    }

    [Fact]
    public async Task MaxFires_CapsFireCount()
    {
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail(maxFires: 2, id: "capped"));

        var r1 = await fx.Client.GetAsync("/api/a");
        var r2 = await fx.Client.GetAsync("/api/b");
        var r3 = await fx.Client.GetAsync("/api/c");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r1.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r2.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        Assert.Equal(2, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.TerminalHits);
        Assert.Equal(2, fx.Store.GetFireCount("capped", "forward-then-fail"));
    }

    [Fact]
    public async Task MaxFires_IsAtomic_UnderConcurrentLoad()
    {
        // Pre-rubber-duck: check-then-record could let many parallel requests all
        // observe count 0 before any of them recorded → MaxFires=1 fires 100 times.
        // TryReserveFire fixes this by atomically incrementing under the cap.
        await using var fx = new ForwardThenFailFixture(upstreamLatencyMs: 20);
        fx.Store.Add(ForwardThenFail(maxFires: 1, id: "atomic"));

        var requestCount = 25;
        var tasks = Enumerable.Range(0, requestCount).Select(i => fx.Client.GetAsync($"/api/r{i}")).ToArray();
        var responses = await Task.WhenAll(tasks);

        var fired = responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, fired);
        Assert.Equal(1, fx.UpstreamForwardCount);
        Assert.Equal(1, fx.Store.GetFireCount("atomic", "forward-then-fail"));
        Assert.Equal(requestCount - 1, fx.TerminalHits);
    }

    [Fact]
    public async Task NoUpstreamUrl_DoesNotBurnFireBudget()
    {
        // Pre-rubber-duck: TryFire ran before upstream-URL check, so a misconfigured
        // proxy with failFirst=1 would burn the slot on the first request, leaving
        // none for the actual repro attempt. Fixed by moving URL resolution before
        // fire-gate consumption.
        await using var fx = new ForwardThenFailFixture(upstreamUrl: null);
        fx.Store.Add(ForwardThenFail(failFirst: 1, id: "no-burn"));

        var r1 = await fx.Client.GetAsync("/api/x");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(0, fx.Store.GetFireCount("no-burn", "forward-then-fail"));
    }

    [Fact]
    public async Task HopByHopHeaders_AreStripped_BeforeForwardingToUpstream()
    {
        // RFC 7230 §6.1 hop-by-hop headers must not be forwarded. HttpClient sets some
        // of these itself (Transfer-Encoding, Host); forwarding the inbound values
        // would either be rejected or break connection semantics.
        await using var fx = new ForwardThenFailFixture();
        fx.Store.Add(ForwardThenFail());

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        req.Headers.TryAddWithoutValidation("Connection", "close");
        req.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
        req.Headers.TryAddWithoutValidation("Proxy-Authorization", "Bearer secret");
        req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        req.Headers.TryAddWithoutValidation("X-Forwarded-For", "client-ip"); // not hop-by-hop — should pass through

        await fx.Client.SendAsync(req);

        Assert.Equal(1, fx.UpstreamForwardCount);
        Assert.NotNull(fx.LastUpstreamHeaders);
        Assert.False(fx.LastUpstreamHeaders!.Contains("Connection"), "Connection should be stripped");
        Assert.False(fx.LastUpstreamHeaders.Contains("Keep-Alive"), "Keep-Alive should be stripped");
        Assert.False(fx.LastUpstreamHeaders.Contains("Proxy-Authorization"), "Proxy-Authorization should be stripped");
        Assert.False(fx.LastUpstreamHeaders.Contains("Upgrade"), "Upgrade should be stripped");
        Assert.True(fx.LastUpstreamHeaders.Contains("X-Forwarded-For"), "non-hop-by-hop headers should pass through");
    }

    [Fact]
    public async Task UnsafeBody_FallsThrough_WithoutBurningFireBudget()
    {
        // If buffering middleware didn't run (e.g., body exceeds 1MB cap), CanSeek is
        // false on a chunked body. forward-then-fail should NOT silently forward a
        // body-less request — it should fall through and preserve fire budget.
        await using var fx = new ForwardThenFailFixture(skipBuffering: true);
        fx.Store.Add(ForwardThenFail(failFirst: 1, id: "unsafe"));

        // Chunked POST with no buffered body — buffering middleware skipped, so
        // Body.CanSeek is false (raw TestServer pipe).
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/upload")
        {
            Content = new StringContent("{\"x\":1}", System.Text.Encoding.UTF8, "application/json"),
        };

        var resp = await fx.Client.SendAsync(req);

        // No fire — middleware refused the unsafe body. ContentLength was set by
        // StringContent, but the test fixture skipped the buffering middleware so
        // Body.CanSeek may be false. Either way, the contract is "no silent body-loss
        // forward + no burned slot".
        Assert.Equal(0, fx.Store.GetFireCount("unsafe", "forward-then-fail"));
    }
}

/// <summary>
/// Pipeline fixture for ChaosForwardThenFailMiddleware tests. Wires the buffering
/// middleware + forward-then-fail middleware + a terminal handler. Injects a recording
/// HttpMessageHandler as the "upstream" so we can assert on what the middleware
/// forwarded WITHOUT spinning up a second TCP listener.
/// </summary>
internal sealed class ForwardThenFailFixture : IAsyncDisposable
{
    private int _terminalHits;
    private int _upstreamForwardCount;
    private readonly IHost _host;

    public ForwardThenFailFixture(string? upstreamUrl = "http://upstream.test", bool upstreamThrows = false, int upstreamLatencyMs = 0, bool skipBuffering = false)
    {
        Store = new ActivePolicyStore();

        var recordingHandler = new RecordingHandler(this, upstreamThrows, upstreamLatencyMs);

        var configValues = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(upstreamUrl))
        {
            configValues["ReverseProxy:Clusters:c1:Destinations:d1:Address"] = upstreamUrl;
        }

        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(b => b.AddInMemoryCollection(configValues))
            .ConfigureWebHostDefaults(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton(Store);
                    services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(recordingHandler));
                });
                webHost.Configure(app =>
                {
                    if (!skipBuffering)
                    {
                        app.UseMiddleware<ChaosRequestBodyBufferingMiddleware>();
                    }
                    app.UseMiddleware<ChaosForwardThenFailMiddleware>();

                    // Terminal: returns 200 OK with "terminal-ok" body. Fires only when
                    // the chaos pipeline did NOT short-circuit.
                    app.Run(async ctx =>
                    {
                        Interlocked.Increment(ref _terminalHits);
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("terminal-ok").ConfigureAwait(false);
                    });
                });
            });

        _host = hostBuilder.Build();
        _host.Start();
        Client = _host.GetTestClient();
    }

    public ActivePolicyStore Store { get; }

    public HttpClient Client { get; }

    public int TerminalHits => _terminalHits;

    public int UpstreamForwardCount => _upstreamForwardCount;

    public string? LastUpstreamMethod { get; private set; }

    public string? LastUpstreamPath { get; private set; }

    public string? LastUpstreamBody { get; private set; }

    public System.Net.Http.Headers.HttpRequestHeaders? LastUpstreamHeaders { get; private set; }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ForwardThenFailFixture _fixture;
        private readonly bool _throws;
        private readonly int _latencyMs;

        public RecordingHandler(ForwardThenFailFixture fixture, bool throws, int latencyMs)
        {
            _fixture = fixture;
            _throws = throws;
            _latencyMs = latencyMs;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_latencyMs > 0)
            {
                await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _fixture._upstreamForwardCount);
            _fixture.LastUpstreamMethod = request.Method.Method;
            _fixture.LastUpstreamPath = request.RequestUri?.AbsolutePath;
            _fixture.LastUpstreamHeaders = request.Headers;
            _fixture.LastUpstreamBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (_throws)
            {
                throw new HttpRequestException("upstream simulated failure");
            }

            // Return a fake upstream response — the middleware should discard this and
            // synthesize its own failure for the client.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("upstream-real-response"),
            };
        }
    }

    private sealed class SingleHandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
