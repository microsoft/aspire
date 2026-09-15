// <copyright file="ChaosDropResponseMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Drops the response on the floor instead of forwarding through YARP. Iterates the
/// <see cref="ActivePolicyStore"/> and applies the first matching policy's
/// DropResponseConfig (per D12, first-installed-wins on overlap per transform type).
///
/// <para>
/// Semantics: the pipeline short-circuits BEFORE the upstream call. The client sees a
/// hung request that terminates only when its own HttpClient.Timeout fires (or its
/// CancellationToken is signaled). Useful for exercising client-side timeout + retry
/// behavior. We do NOT proactively reset the connection - we just stop processing the
/// request, letting the server-side request-aborted token eventually cancel things on
/// its own timer if the client gives up.
/// </para>
/// </summary>
internal sealed class ChaosDropResponseMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosDropResponseMiddleware> _logger;

    public ChaosDropResponseMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosDropResponseMiddleware> logger)
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

        var policy = FindMatchingPolicyWithDrop(context.Request);
        if (policy is not null && TryFire(context, policy, out var fireReason))
        {
            _logger.LogDebug(
                "Dropping response on {Method} {Path} via policy {PolicyId}; client will see a hang until its timeout fires",
                context.Request.Method, context.Request.Path, policy.Id);

            TagActivity(policy, fireReason);
            _store.RecordFire(policy.Id, "drop-response");
            ChaosMeter.RecordFire(policy.Id, "drop-response", fireReason);

            // Wait on the request-aborted token so the test server cleans up promptly when
            // the client cancels. Wait indefinitely otherwise (the design - we want the
            // client to feel the full hang). The token's cancellation triggers a
            // TaskCanceledException which propagates as a connection-reset to the client.
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Expected - client gave up. Swallow so the pipeline doesn't 500.
            }

            return; // never reach upstream
        }

        await _next(context).ConfigureAwait(false);
    }

    private ActivePolicy? FindMatchingPolicyWithDrop(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.DropResponse is null)
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
        var drop = policy.DropResponse!;

        // Global MaxFires cap: peek the current fire count BEFORE deciding to fire.
        // Once the policy has fired this many times across all request keys, no further
        // matches fire. Complements per-request-key FailFirst for protocols that
        // fan out across many keys (e.g. DTFx Azure Queue Storage POSTs across
        // multiple control-queue partitions).
        if (drop.MaxFires.HasValue && _store.GetFireCount(policy.Id, "drop-response") >= drop.MaxFires.Value)
        {
            fireReason = string.Empty;
            return false;
        }

        if (_store.ConsumeFireOnceForPolicy(policy.Id, "drop-response"))
        {
            fireReason = "fire-once";
            return true;
        }
        if (_store.ConsumeFireOnce("drop-response"))
        {
            fireReason = "fire-once";
            return true;
        }

        if (drop.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot("drop-response", policy.Id, requestKey, drop.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (drop.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (drop.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < drop.Probability)
        {
            fireReason = "probability";
            return true;
        }
        fireReason = string.Empty;
        return false;
    }

    private static void TagActivity(ActivePolicy policy, string fireReason)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.drop.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.drop.fire_reason", fireReason);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.drop-response", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "drop-response");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
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
