// <copyright file="ChaosSlowResponseMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Synthesizes a successful response and streams the body at a configurable
/// bytes/sec rate. Distinct from latency (which delays once then forwards at full
/// speed) and partial-response (which aborts mid-stream): this transform delivers
/// the FULL body but slowly. Models "slow upstream that completes successfully but
/// takes forever per byte" - the failure mode for clients that have per-read
/// timeouts shorter than the full response time.
/// </summary>
internal sealed class ChaosSlowResponseMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string Bucket = "slow-response";

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosSlowResponseMiddleware> _logger;

    public ChaosSlowResponseMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosSlowResponseMiddleware> logger)
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

        var policy = FindMatchingPolicyWithSlow(context.Request);
        if (policy is not null && TryFire(context, policy, out var fireReason))
        {
            var sr = policy.SlowResponse!;
            _logger.LogDebug(
                "Streaming slow response on {Method} {Path} via policy {PolicyId}: {Body} bytes at {Rate} bytes/sec",
                context.Request.Method, context.Request.Path, policy.Id,
                sr.Body.Length, sr.BytesPerSecond);

            TagActivity(policy, fireReason, sr);
            _store.RecordFire(policy.Id, Bucket);
            ChaosMeter.RecordFire(policy.Id, Bucket, fireReason);

            context.Response.StatusCode = sr.Status;
            context.Response.ContentType = sr.ContentType ?? "application/octet-stream";
            // Advertise full length so clients reading Content-Length know to keep waiting.
            context.Response.ContentLength = sr.Body.Length;

            await StreamSlowlyAsync(context, sr).ConfigureAwait(false);
            return; // never reach upstream
        }

        await _next(context).ConfigureAwait(false);
    }

    private static async Task StreamSlowlyAsync(HttpContext context, SlowResponseConfig sr)
    {
        var chunkSize = Math.Max(1, sr.ChunkSize);
        // Time per chunk = chunkSize / bytesPerSecond * 1000 ms; round up so the
        // observed throughput is at-or-below the configured rate (better to be slow
        // than fast for chaos purposes).
        var delayPerChunkMs = (int)Math.Ceiling((double)chunkSize / sr.BytesPerSecond * 1000.0);

        var offset = 0;
        while (offset < sr.Body.Length && !context.RequestAborted.IsCancellationRequested)
        {
            var remaining = sr.Body.Length - offset;
            var thisChunk = Math.Min(chunkSize, remaining);
            try
            {
                await context.Response.Body.WriteAsync(sr.Body.AsMemory(offset, thisChunk), context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // client gave up; stop streaming.
            }
            offset += thisChunk;

            if (offset < sr.Body.Length)
            {
                try
                {
                    await Task.Delay(delayPerChunkMs, context.RequestAborted).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
    }

    private ActivePolicy? FindMatchingPolicyWithSlow(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.SlowResponse is null)
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

        var sr = policy.SlowResponse!;

        if (sr.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot(Bucket, policy.Id, requestKey, sr.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (sr.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (sr.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < sr.Probability)
        {
            fireReason = "probability";
            return true;
        }
        fireReason = string.Empty;
        return false;
    }

    private static void TagActivity(ActivePolicy policy, string fireReason, SlowResponseConfig sr)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.slow_response.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.slow_response.fire_reason", fireReason);
            activity.SetTag("chaos.proxy.slow_response.bytes_per_second", sr.BytesPerSecond);
            activity.SetTag("chaos.proxy.slow_response.body_size", sr.Body.Length);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.slow-response", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "slow-response");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
        fireActivity?.SetTag("chaos.proxy.slow_response.bytes_per_second", sr.BytesPerSecond);
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
