// <copyright file="ChaosIdempotencyCollisionMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Simulates idempotency-key collision detection. When a request carries the
/// configured key header AND the same value has been seen within the policy's
/// sliding window, the proxy short-circuits with a configured response (default 409).
/// Requests without the key header always pass through (no possible collision).
///
/// <para>
/// Reproduces the common backend safeguard pattern of refusing duplicate idempotency
/// keys. Lets tests validate that the client surfaces the collision scenario without
/// needing the real backend's idempotency cache to be primed.
/// </para>
/// </summary>
internal sealed class ChaosIdempotencyCollisionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosIdempotencyCollisionMiddleware> _logger;

    public ChaosIdempotencyCollisionMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosIdempotencyCollisionMiddleware> logger)
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

        var policy = FindMatchingPolicyWithIdempotencyCollision(context.Request);
        if (policy is not null)
        {
            var ic = policy.IdempotencyCollision!;
            if (context.Request.Headers.TryGetValue(ic.KeyHeaderName, out var keyValues)
                && keyValues.Count > 0
                && !string.IsNullOrEmpty(keyValues[0]))
            {
                var key = keyValues[0]!;
                var firstSighting = _store.TryRecordIdempotencyKey(policy.Id, key, ic.WindowMs);

                if (!firstSighting)
                {
                    _logger.LogDebug(
                        "Idempotency collision on {Method} {Path} via policy {PolicyId} (key={Key}); returning {Status}",
                        context.Request.Method, context.Request.Path, policy.Id, key, ic.Status);

                    TagActivity(policy, ic, key);
                    _store.RecordFire(policy.Id, "idempotency-collision");
                    ChaosMeter.RecordFire(policy.Id, "idempotency-collision", "collision");

                    context.Response.StatusCode = ic.Status;
                    if (ic.Headers is not null)
                    {
                        foreach (var kv in ic.Headers)
                        {
                            context.Response.Headers[kv.Key] = kv.Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(ic.Body))
                    {
                        context.Response.ContentType = ic.ContentType ?? "text/plain; charset=utf-8";
                        var bytes = Encoding.UTF8.GetBytes(ic.Body);
                        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
                    }

                    return;
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private ActivePolicy? FindMatchingPolicyWithIdempotencyCollision(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.IdempotencyCollision is null)
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

    private static void TagActivity(ActivePolicy policy, IdempotencyCollisionConfig ic, string key)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.idempotency_collision.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.idempotency_collision.status", ic.Status);
            activity.SetTag("chaos.proxy.idempotency_collision.key", key);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.idempotency-collision", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "idempotency-collision");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.idempotency_collision.status", ic.Status);
    }
}
