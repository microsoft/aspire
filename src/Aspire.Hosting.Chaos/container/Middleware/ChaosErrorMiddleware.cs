// <copyright file="ChaosErrorMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Middleware that short-circuits with a configured HTTP error response instead of
/// forwarding through YARP. Iterates the <see cref="ActivePolicyStore"/> and applies the
/// first matching policy's ErrorConfig (per D12, first-installed-wins on overlap per
/// transform type). Pipeline order is latency -> error -> replay-duplicate -> YARP so
/// the client observes the configured delay AND THEN the error.
/// </summary>
internal sealed class ChaosErrorMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosErrorMiddleware> _logger;

    public ChaosErrorMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosErrorMiddleware> logger)
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

        var policy = FindMatchingPolicyWithError(context.Request);
        if (policy is not null && TryFire(context, policy, out var fireReason))
        {
            var err = policy.Error!;
            _logger.LogDebug(
                "Injecting HTTP {Status} on {Method} {Path} via policy {PolicyId}",
                err.Status, context.Request.Method, context.Request.Path, policy.Id);

            TagActivity(policy, err, fireReason);
            _store.RecordFire(policy.Id, "error", $"{context.Request.Method} {context.Request.Path}");
            ChaosMeter.RecordFire(policy.Id, "error", fireReason);

            context.Response.StatusCode = err.Status;

            // Apply custom headers BEFORE writing the body (response headers cannot be set
            // after writes begin). Used by Azure-shaped transforms in the .Azure companion
            // (e.g., x-ms-retry-after-ms for Cosmos throttling, Retry-After for Key Vault).
            if (err.Headers is not null)
            {
                foreach (var kv in err.Headers)
                {
                    context.Response.Headers[kv.Key] = kv.Value;
                }
            }

            if (!string.IsNullOrEmpty(err.Body))
            {
                context.Response.ContentType = err.ContentType ?? "text/plain; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(err.Body);
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            }

            return; // short-circuit
        }

        await _next(context).ConfigureAwait(false);
    }

    private ActivePolicy? FindMatchingPolicyWithError(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.Error is null)
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
        if (_store.ConsumeFireOnceForPolicy(policy.Id, "error"))
        {
            fireReason = "fire-once";
            return true;
        }
        if (_store.ConsumeFireOnce("error"))
        {
            fireReason = "fire-once";
            return true;
        }

        var err = policy.Error!;

        if (err.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot("error", policy.Id, requestKey, err.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (err.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (err.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < err.Probability)
        {
            fireReason = "probability";
            return true;
        }
        fireReason = string.Empty;
        return false;
    }

    private static void TagActivity(ActivePolicy policy, ErrorConfig err, string fireReason)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.error.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.error.fire_reason", fireReason);
            activity.SetTag("chaos.proxy.error.status", err.Status);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.error", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "error");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
        fireActivity?.SetTag("chaos.proxy.error.status", err.Status);
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
