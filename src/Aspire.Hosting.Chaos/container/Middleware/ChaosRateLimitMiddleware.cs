// <copyright file="ChaosRateLimitMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Sliding-window rate-limit middleware. When the configured rate is exceeded the
/// request short-circuits with the policy's configured status + headers; otherwise
/// the request flows to the next stage normally.
///
/// <para>
/// Pipeline position: after error (an error-policy match wins outright; no point
/// counting errored requests against the rate budget) and before drop/replay/upstream.
/// </para>
///
/// <para>
/// Per-request-key bucketing follows the same three-tier derivation as failFirst
/// (x-ms-client-request-id -> Idempotency-Key -> method+path) so the same conceptual
/// caller is rate-limited as one stream even across many request paths.
/// </para>
/// </summary>
internal sealed class ChaosRateLimitMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string Bucket = "rate-limit";

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosRateLimitMiddleware> _logger;

    public ChaosRateLimitMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosRateLimitMiddleware> logger)
    {
        _next = next;
        _store = store;
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

        var policy = FindMatchingPolicyWithRateLimit(context.Request);
        if (policy is not null)
        {
            // Fire-once forces an immediate rate-limit response on the next matching
            // request - regardless of the actual sliding-window state. Useful for the
            // dashboard fire-once-rate-limit command.
            var requestKey = DeriveRequestKey(context);
            var fired = _store.ConsumeFireOnceForPolicy(policy.Id, Bucket) || _store.ConsumeFireOnce(Bucket);
            var admitted = fired
                ? false
                : _store.TryAdmitRateLimitedRequest(Bucket, policy.Id, requestKey, policy.RateLimit!.RequestsPerWindow, policy.RateLimit.WindowMs);

            if (!admitted)
            {
                var rl = policy.RateLimit!;
                _logger.LogDebug(
                    "Rate-limited {Method} {Path} via policy {PolicyId} (status {Status}, fire-once={FireOnce})",
                    context.Request.Method, context.Request.Path, policy.Id, rl.Status, fired);

                TagActivity(policy, fired ? "fire-once" : "rate-exceeded", rl);
                _store.RecordFire(policy.Id, "rate-limit");
                ChaosMeter.RecordFire(policy.Id, "rate-limit", fired ? "fire-once" : "rate-exceeded");

                context.Response.StatusCode = rl.Status;
                if (rl.Headers is not null)
                {
                    foreach (var kv in rl.Headers)
                    {
                        context.Response.Headers[kv.Key] = kv.Value;
                    }
                }

                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private ActivePolicy? FindMatchingPolicyWithRateLimit(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.RateLimit is null)
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

    private static void TagActivity(ActivePolicy policy, string fireReason, RateLimitConfig rl)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.rate_limit.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.rate_limit.fire_reason", fireReason);
            activity.SetTag("chaos.proxy.rate_limit.status", rl.Status);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.rate-limit", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "rate-limit");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
        fireActivity?.SetTag("chaos.proxy.rate_limit.status", rl.Status);
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

        // Anonymous traffic falls into a SINGLE bucket per policy - rate-limit semantics
        // are "throttle the matcher as a whole", different from failFirst's "throttle the
        // SAME request being retried". Use a constant so different paths under the same
        // matcher count against the same budget.
        return "anon";
    }
}
