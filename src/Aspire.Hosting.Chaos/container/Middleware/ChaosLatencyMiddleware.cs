// <copyright file="ChaosLatencyMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Middleware that injects latency on requests forwarded through the proxy.
/// Iterates the <see cref="ActivePolicyStore"/> and applies the first matching policy's
/// LatencyConfig (per D12, first-installed-wins on overlap per transform type).
/// </summary>
internal sealed class ChaosLatencyMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosLatencyMiddleware> _logger;

    public ChaosLatencyMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosLatencyMiddleware> logger)
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

        // Global pause - flipped via /chaos/pause + /chaos/resume endpoints (or the
        // pause-faults / resume-faults dashboard commands).
        if (_store.IsPaused)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policy = FindMatchingPolicyWithLatency(context.Request);
        if (policy is not null && TryFire(context, policy, out var fireReason))
        {
            var delayMs = ComputeDelay(policy.Latency!);
            _logger.LogDebug(
                "Injecting {DelayMs}ms latency on {Method} {Path} via policy {PolicyId}",
                delayMs, context.Request.Method, context.Request.Path, policy.Id);
            _store.RecordFire(policy.Id, "latency");
            ChaosMeter.RecordFire(policy.Id, "latency", fireReason);
            TagActivity(policy, fireReason, delayMs);
            await Task.Delay(delayMs, context.RequestAborted).ConfigureAwait(false);
        }

        await _next(context).ConfigureAwait(false);
    }

    private static void TagActivity(ActivePolicy policy, string fireReason, int delayMs)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag("chaos.proxy.fired", true);
        activity.SetTag("chaos.proxy.latency.policy_id", policy.Id);
        activity.SetTag("chaos.proxy.latency.fire_reason", fireReason);
        activity.SetTag("chaos.proxy.latency.delay_ms", delayMs);

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.latency", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "latency");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
        fireActivity?.SetTag("chaos.proxy.latency.delay_ms", delayMs);
    }

    private ActivePolicy? FindMatchingPolicyWithLatency(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.Latency is null)
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
        // Per-policy fire-once takes precedence; falls through to global if not armed.
        // Consume is destructive so check per-policy first to avoid burning the global
        // trigger when a per-policy one is also armed.
        if (_store.ConsumeFireOnceForPolicy(policy.Id, "latency"))
        {
            fireReason = "fire-once";
            return true;
        }
        if (_store.ConsumeFireOnce("latency"))
        {
            fireReason = "fire-once";
            return true;
        }

        var latency = policy.Latency!;

        if (latency.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot("latency", policy.Id, requestKey, latency.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (latency.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (latency.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < latency.Probability)
        {
            fireReason = "probability";
            return true;
        }
        fireReason = string.Empty;
        return false;
    }

    private static int ComputeDelay(LatencyConfig latency)
    {
        if (latency.MinMs >= latency.MaxMs)
        {
            return latency.MinMs;
        }

        return Random.Shared.Next(latency.MinMs, latency.MaxMs + 1);
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
