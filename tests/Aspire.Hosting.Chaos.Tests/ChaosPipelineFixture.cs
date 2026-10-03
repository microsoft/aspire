// <copyright file="ChaosPipelineFixture.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Middleware;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Policy.Profiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// In-process pipeline that wires the three chaos middlewares around a terminal
/// handler. Mirrors the production pipeline order (latency -> error -> replay -> terminal)
/// but skips YARP itself: the terminal handler simulates upstream by recording each
/// request that reached it and returning <c>200 OK</c> with a marker header.
/// </summary>
/// <remarks>
/// Replay-duplicate's background fan-out is disabled by NOT configuring an upstream URL -
/// the middleware silently no-ops the replay when <c>ReverseProxy:Clusters:c1:Destinations:d1:Address</c>
/// is unset, which is what we want here. (We assert the in-band behavior - that the
/// original request still flows through - rather than the background replay timing,
/// which is non-deterministic by design.)
/// </remarks>
internal sealed class ChaosPipelineFixture : IAsyncDisposable
{
    private readonly IHost _host;

    public ChaosPipelineFixture()
        : this(null)
    {
    }

    public ChaosPipelineFixture(FaultProfileRegistry? profiles)
    {
        UpstreamCallCount = 0;
        Store = new ActivePolicyStore();

        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton(Store);
                    services.AddSingleton(profiles ?? FaultProfileRegistry.CreateDefault());
                    services.AddHttpClient();
                });
                webHost.Configure(app =>
                {
                    app.UseMiddleware<ChaosRandomFaultMiddleware>();
                    app.UseMiddleware<ChaosLatencyMiddleware>();
                    app.UseMiddleware<ChaosHeaderTamperMiddleware>();
                    app.UseMiddleware<ChaosIdempotencyCollisionMiddleware>();
                    app.UseMiddleware<ChaosErrorMiddleware>();
                    app.UseMiddleware<ChaosRateLimitMiddleware>();
                    app.UseMiddleware<ChaosPartialResponseMiddleware>();
                    app.UseMiddleware<ChaosSlowResponseMiddleware>();
                    app.UseMiddleware<ChaosDropResponseMiddleware>();
                    app.UseMiddleware<ChaosReplayDuplicateMiddleware>();

                    // Terminal: echoes a hit count + the request path.
                    app.Run(async ctx =>
                    {
                        Interlocked.Increment(ref _upstreamCalls);
                        LastUpstreamPath = ctx.Request.Path.Value;
                        // Snapshot the request headers the upstream actually received so
                        // request-side tamper tests can assert what flowed through.
                        LastUpstreamRequestHeaders = ctx.Request.Headers.ToDictionary(
                            h => h.Key,
                            h => h.Value.ToArray(),
                            StringComparer.OrdinalIgnoreCase);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.Headers["X-Upstream-Hit"] = "true";
                        await ctx.Response.WriteAsync("upstream-ok").ConfigureAwait(false);
                    });
                });
            });

        _host = hostBuilder.Build();
        _host.Start();
        Client = _host.GetTestClient();
    }

    private int _upstreamCalls;

    public ActivePolicyStore Store { get; }

    public HttpClient Client { get; }

    public int UpstreamCallCount { get => _upstreamCalls; private set => _upstreamCalls = value; }

    public string? LastUpstreamPath { get; private set; }

    public IReadOnlyDictionary<string, string[]>? LastUpstreamRequestHeaders { get; private set; }

    public void Reset()
    {
        Interlocked.Exchange(ref _upstreamCalls, 0);
        LastUpstreamPath = null;
        LastUpstreamRequestHeaders = null;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}
