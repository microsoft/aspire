// <copyright file="ChaosForwardThenFailMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;
using Microsoft.Extensions.Configuration;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Forwards the request to the upstream destination (so the upstream-side state change
/// actually happens), discards the upstream response, then returns a configured
/// retryable failure to the client. The ONLY transform in this proxy that lets
/// upstream commit while the client sees a failure.
///
/// <para>
/// Why it exists: every other chaos transform short-circuits BEFORE upstream is called
/// (see <see cref="ChaosDropResponseMiddleware"/>, <see cref="ChaosErrorMiddleware"/>,
/// etc.). That breaks E2E reproductions of "state-guard fires on the retry" bug
/// classes — e.g. the GW Worker activity POSTs <c>/evaluateScenarios</c> to BE, BE
/// commits an eval, but the client never sees the response → DTFx replays the
/// activity → replay POST hits the real BE state guard and gets a real 409 with a
/// real operationId. This middleware reproduces exactly that flow at the wire.
/// </para>
///
/// <para>
/// Implementation notes:
/// </para>
/// <list type="bullet">
///   <item>Upstream URL is read from YARP config (<c>ReverseProxy:Clusters:c1:Destinations:d1:Address</c>) — the same key the AppHost wires for the mesh proxies. We bypass YARP for forwarding because YARP owns the response stream and there's no clean way to suppress its body from a pre-YARP middleware.</item>
///   <item>The upstream call uses its own <c>CancellationTokenSource</c> (not the client's <c>RequestAborted</c>) so the upstream-side commit completes even if the client disconnects. This is the whole point — without it, cancellation propagation would unwind the upstream side and we'd be no different from <c>DropResponse</c>.</item>
///   <item>If the upstream URL isn't configured or the forward throws, we STILL return the configured failure to the client. The whole transform is best-effort on the upstream side — the contract with the caller is "client sees configured failure"; the upstream side is a side-effect we attempt but can't guarantee.</item>
/// </list>
/// </summary>
internal sealed class ChaosForwardThenFailMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string Bucket = "forward-then-fail";

    /// <summary>
    /// Hop-by-hop headers that must not be forwarded per RFC 7230 §6.1. Same list as
    /// <see cref="ChaosReplayDuplicateMiddleware"/>. Forwarding these would either break
    /// the upstream connection or leak our proxy's per-hop state (e.g., Authorization
    /// for the proxy itself).
    /// </summary>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
    };

    /// <summary>Cap on the discarded upstream response body. Anything beyond this is fine to leave; we close the connection.</summary>
    private const int MaxDiscardBytes = 4 * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChaosForwardThenFailMiddleware> _logger;

    public ChaosForwardThenFailMiddleware(
        RequestDelegate next,
        ActivePolicyStore store,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChaosForwardThenFailMiddleware> logger)
    {
        _next = next;
        _store = store;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/chaos"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_store.IsPaused)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policy = FindMatchingPolicy(context.Request);
        if (policy is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Resolve upstream URL BEFORE consuming any fire budget so a misconfigured
        // proxy doesn't burn fire-once / failFirst slots on no-op fallthroughs.
        var upstreamUrl = ResolveUpstreamUrl();
        if (string.IsNullOrEmpty(upstreamUrl))
        {
            _logger.LogWarning(
                "forward-then-fail policy {PolicyId} would fire but no upstream URL configured (ReverseProxy:Clusters:c1:Destinations:d1:Address); falling through without consuming fire budget",
                policy.Id);
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Verify body safety BEFORE firing — refuse to fire on requests whose body we
        // couldn't faithfully replay to upstream. Forwarding a body-less mutating
        // request would either succeed (creating wrong state at upstream) or fail
        // (depriving the test of the expected commit). Either way, silently downgrading
        // is worse than falling through.
        if (HasUnsafeBodyForReplay(context.Request))
        {
            _logger.LogWarning(
                "forward-then-fail policy {PolicyId} cannot safely replay request body for {Method} {Path} (ContentLength={ContentLength}, CanSeek={CanSeek}); falling through without consuming fire budget. Possible causes: body exceeds buffering cap (1MB), chunked transfer with no buffered body, or buffering middleware not registered.",
                policy.Id, context.Request.Method, context.Request.Path, context.Request.ContentLength, context.Request.Body.CanSeek);
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!TryFire(context, policy, out var fireReason))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        await ForwardAndDiscardAsync(context, policy.ForwardThenFail!, upstreamUrl, policy.Id).ConfigureAwait(false);

        TagActivity(policy, fireReason);
        // RecordFire is now redundant for the MaxFires path (TryReserveFire already
        // recorded it atomically) — but TryFire also has non-MaxFires paths
        // (probability, failFirst, fire-once) that need explicit recording. The store's
        // counter is idempotent across pathways: TryReserveFire reserves slot N, and
        // RecordFire under the alternate paths increments to N+1 — both observable to
        // GetFireCount. No double-count for the MaxFires path because we skip RecordFire
        // there (see RecordFireIfNotReserved).
        RecordFireIfNotReserved(policy, fireReason);
        ChaosMeter.RecordFire(policy.Id, Bucket, fireReason);

        await WriteFailureResponseAsync(context, policy.ForwardThenFail!, policy.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the request has a body we cannot faithfully replay to upstream.
    /// Refusing to fire keeps the transform's contract honest: "client sees configured
    /// failure" implies "upstream got the same request the client sent". If we can't
    /// guarantee that, we'd rather fall through (no chaos) than send a body-less request.
    /// </summary>
    private static bool HasUnsafeBodyForReplay(HttpRequest request)
    {
        // No body declared and no transfer-encoding chunked: safe (forward with no body).
        var hasChunkedTransfer = request.Headers.TryGetValue("Transfer-Encoding", out var teValues)
            && teValues.Any(v => v?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true);

        if (!hasChunkedTransfer && (request.ContentLength is null or 0))
        {
            return false;
        }

        // Body declared (either ContentLength > 0 or chunked). We MUST be able to
        // re-read it. The buffering middleware enables Body.CanSeek for any request
        // matching a forwardThenFail policy. If CanSeek is false here, buffering didn't
        // run (oversized body, pipeline mis-wiring) — unsafe.
        return !request.Body.CanSeek;
    }

    /// <summary>
    /// Records the fire UNLESS it was already recorded by TryReserveFire (the MaxFires
    /// path). Returns silently when fireReason indicates the reservation pathway —
    /// avoids double-counting in metrics + dashboard fire counters.
    /// </summary>
    private void RecordFireIfNotReserved(ActivePolicy policy, string fireReason)
    {
        if (fireReason == "max-fires-reserved")
        {
            return;
        }
        _store.RecordFire(policy.Id, Bucket);
    }

    private ActivePolicy? FindMatchingPolicy(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.ForwardThenFail is null)
            {
                continue;
            }
            if (policy.Matcher is not null && !policy.Matcher.Matches(request))
            {
                continue;
            }
            return policy;
        }
        return null;
    }

    private bool TryFire(HttpContext context, ActivePolicy policy, out string fireReason)
    {
        var cfg = policy.ForwardThenFail!;

        // MaxFires is the slow-work cap: forward-then-fail's upstream HTTP call takes
        // hundreds of ms, during which many concurrent requests could observe the same
        // pre-fire count of N and ALL pass the check (then ALL fire). TryReserveFire
        // atomically increments the counter under the cap so only the first N requests
        // win the race. Subsequent paths (fire-once, failFirst, probability) write to
        // the same counter via RecordFire, but they're guarded by their own atomic
        // primitives (ConsumeFireOnce, ConsumeFailFirstSlot) so the slow-work race
        // doesn't apply.
        if (cfg.MaxFires.HasValue)
        {
            if (!_store.TryReserveFire(policy.Id, Bucket, cfg.MaxFires.Value))
            {
                fireReason = string.Empty;
                return false;
            }
            fireReason = "max-fires-reserved";
            return true;
        }

        if (_store.ConsumeFireOnceForPolicy(policy.Id, Bucket))
        {
            fireReason = "fire-once";
            return true;
        }
        if (_store.ConsumeFireOnce(Bucket))
        {
            fireReason = "fire-once";
            return true;
        }

        if (cfg.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot(Bucket, policy.Id, requestKey, cfg.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (cfg.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (cfg.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < cfg.Probability)
        {
            fireReason = "probability";
            return true;
        }
        fireReason = string.Empty;
        return false;
    }

    private string? ResolveUpstreamUrl()
    {
        // The mesh proxies wire one cluster (c1) with one destination (d1). Read that
        // address directly. If the proxy is configured differently in the future, this
        // can be extended to enumerate IProxyConfigProvider instead.
        return _configuration["ReverseProxy:Clusters:c1:Destinations:d1:Address"];
    }

    private async Task ForwardAndDiscardAsync(HttpContext context, ForwardThenFailConfig cfg, string upstreamUrl, string policyId)
    {
        // Build the upstream URI preserving raw path encoding (escaped segments,
        // canonical separators) — Request.Path.Value would normalize and could break
        // signed URLs / encoded slashes. PathString.ToUriComponent gives us the
        // wire-format path the client sent.
        var pathPart = context.Request.Path.ToUriComponent();
        var queryPart = context.Request.QueryString.ToUriComponent();
        var upstreamRequestUri = new Uri(upstreamUrl.TrimEnd('/') + pathPart + queryPart, UriKind.Absolute);

        // Use a dedicated CTS — NOT context.RequestAborted. The whole point of this
        // transform is to let the upstream commit even if the client gives up. If we
        // propagated client cancellation, an HTTP server that listens for it (e.g.,
        // ASP.NET Core's RequestAborted-aware controllers) might cancel the commit and
        // we'd be no better than DropResponse.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.UpstreamTimeoutSeconds));

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan; // CTS owns the timeout.

            using var forwardRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamRequestUri);

            // Copy headers, stripping hop-by-hop headers per RFC 7230 §6.1.
            // Forwarding Connection / Transfer-Encoding / etc. would either break the
            // upstream connection (HttpClient sets these itself) or leak proxy state.
            // Content-* headers are silently dropped by request-headers collection
            // and re-attached via content.Headers below.
            foreach (var header in context.Request.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                {
                    continue;
                }

                var values = header.Value.ToArray();
                if (!forwardRequest.Headers.TryAddWithoutValidation(header.Key, values))
                {
                    // Content header — attach via content.Headers below if/when content is set.
                }
            }

            // Forward the request body if present. By the time we reach this code path
            // HasUnsafeBodyForReplay() has already rejected requests we can't safely
            // replay, so the only "skip body" case here is "no body declared at all"
            // (GET, HEAD, DELETE without body, etc.).
            var hasBody = (context.Request.ContentLength is > 0) ||
                (context.Request.Headers.TryGetValue("Transfer-Encoding", out var teValues)
                    && teValues.Any(v => v?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true));

            if (hasBody && context.Request.Body.CanSeek)
            {
                var bodyBuffer = new MemoryStream();
                context.Request.Body.Position = 0;
                await context.Request.Body.CopyToAsync(bodyBuffer, cts.Token).ConfigureAwait(false);
                bodyBuffer.Position = 0;

                var content = new StreamContent(bodyBuffer);
                foreach (var header in context.Request.Headers)
                {
                    if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    {
                        content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }
                forwardRequest.Content = content;
            }

            using var response = await client.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            // Drain and discard the response body up to MaxDiscardBytes to free the
            // connection promptly. For a misconfigured upstream returning 10GB we
            // stop reading after the cap and let HttpResponseMessage.Dispose abort
            // the rest — better to close the connection than tie up the proxy.
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var discardBuffer = new byte[8 * 1024];
            long totalDiscarded = 0;
            while (totalDiscarded < MaxDiscardBytes)
            {
                var read = await stream.ReadAsync(discardBuffer.AsMemory(0, (int)Math.Min(discardBuffer.Length, MaxDiscardBytes - totalDiscarded)), cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalDiscarded += read;
            }

            _logger.LogDebug(
                "forward-then-fail upstream call complete for policy {PolicyId}: {Method} {Uri} -> {Status} (discarded {Bytes} bytes)",
                policyId, context.Request.Method, upstreamRequestUri, (int)response.StatusCode, totalDiscarded);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "forward-then-fail upstream call timed out after {Timeout}s for policy {PolicyId}: {Method} {Uri}. Returning configured failure to client anyway.",
                cfg.UpstreamTimeoutSeconds, policyId, context.Request.Method, upstreamRequestUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "forward-then-fail upstream call failed for policy {PolicyId}: {Method} {Uri}. Returning configured failure to client anyway.",
                policyId, context.Request.Method, upstreamRequestUri);
        }
    }

    private async Task WriteFailureResponseAsync(HttpContext context, ForwardThenFailConfig cfg, string policyId)
    {
        // Should be impossible in the current pipeline order — no middleware ahead of
        // us flushes the response. Log loudly if it ever happens so we catch a future
        // pipeline misconfiguration instead of silently committing upstream while the
        // client gets whatever the prior middleware sent.
        if (context.Response.HasStarted)
        {
            _logger.LogError(
                "forward-then-fail policy {PolicyId}: response already started before failure write. Upstream was forwarded but the configured failure CANNOT be written. The client will see whatever the prior middleware sent. This indicates a pipeline ordering bug.",
                policyId);
            return;
        }

        context.Response.StatusCode = cfg.Status;

        if (!string.IsNullOrEmpty(cfg.ContentType))
        {
            context.Response.ContentType = cfg.ContentType;
        }

        if (cfg.Headers is not null)
        {
            foreach (var kv in cfg.Headers)
            {
                context.Response.Headers[kv.Key] = kv.Value;
            }
        }

        if (!string.IsNullOrEmpty(cfg.Body))
        {
            await context.Response.WriteAsync(cfg.Body).ConfigureAwait(false);
        }
    }

    private static void TagActivity(ActivePolicy policy, string fireReason)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.forward_then_fail.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.forward_then_fail.fire_reason", fireReason);
            activity.SetTag("chaos.proxy.forward_then_fail.status", policy.ForwardThenFail!.Status);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.forward-then-fail", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", Bucket);
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
        fireActivity?.SetTag("chaos.proxy.forward_then_fail.status", policy.ForwardThenFail!.Status);
    }

    private static string DeriveRequestKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ClientRequestIdHeader, out var clientId) && !string.IsNullOrEmpty(clientId))
        {
            return $"client:{clientId}";
        }
        if (context.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var idempKey) && !string.IsNullOrEmpty(idempKey))
        {
            return $"idempotency:{idempKey}";
        }

        return $"anon:{context.Request.Method}:{context.Request.Path}";
    }
}
