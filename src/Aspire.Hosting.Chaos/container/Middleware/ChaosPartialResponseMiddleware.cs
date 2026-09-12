// <copyright file="ChaosPartialResponseMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;
using Microsoft.AspNetCore.Http.Features;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Writes a partial response body and then aborts the connection. Distinct from drop
/// (which never writes anything): partial response delivers a valid HTTP status +
/// headers + part of the body, then cuts the stream off mid-flight. The client sees
/// a successful response with Content-Length advertising more bytes than actually
/// arrive, triggering deserialization failures or truncated-stream errors depending
/// on the client library.
///
/// <para>
/// Pipeline position: after error/rate-limit (those short-circuit cleanly with
/// complete responses) and before drop (a partial response is "I deliver something,
/// then break"; drop is "I deliver nothing"). They're mutually exclusive per policy
/// in practice.
/// </para>
/// </summary>
internal sealed class ChaosPartialResponseMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string Bucket = "partial-response";

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosPartialResponseMiddleware> _logger;

    public ChaosPartialResponseMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosPartialResponseMiddleware> logger)
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

        var policy = FindMatchingPolicyWithPartialResponse(context.Request);
        if (policy is not null && TryFire(context, policy, out var fireReason))
        {
            var pr = policy.PartialResponse!;
            _logger.LogDebug(
                "Writing partial response on {Method} {Path} via policy {PolicyId}: {Status}, {Written}/{Advertised} bytes",
                context.Request.Method, context.Request.Path, policy.Id,
                pr.Status, pr.Body.Length, pr.AdvertisedContentLength ?? pr.Body.Length);

            TagActivity(policy, fireReason, pr);
            _store.RecordFire(policy.Id, "partial-response");
            ChaosMeter.RecordFire(policy.Id, "partial-response", fireReason);

            context.Response.StatusCode = pr.Status;
            context.Response.ContentType = pr.ContentType ?? "application/octet-stream";

            // Optional lying-about-length: advertise more bytes than we'll actually send
            // so the client expects a longer stream and errors when it gets cut off.
            if (pr.AdvertisedContentLength.HasValue)
            {
                context.Response.ContentLength = pr.AdvertisedContentLength.Value;
            }

            if (pr.Body.Length > 0)
            {
                await context.Response.Body.WriteAsync(pr.Body, context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }

            // Optional drain window: let the partial bytes leave the kernel before we
            // abort. Without this the client may not see ANY bytes - the abort can race
            // ahead of the buffered write.
            if (pr.AbortAfterMs > 0)
            {
                try
                {
                    await Task.Delay(pr.AbortAfterMs, context.RequestAborted).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Client already gave up - that's fine, fall through to abort.
                }
            }

            // IHttpResponseBodyFeature.CompleteAsync would finish gracefully (with the
            // missing bytes never sent). We want the connection torn down so the client
            // sees a truncated stream / connection reset, which is the actual failure
            // mode in production.
            context.Abort();
            return; // short-circuit; never reach upstream
        }

        await _next(context).ConfigureAwait(false);
    }

    private ActivePolicy? FindMatchingPolicyWithPartialResponse(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.PartialResponse is null)
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

        var pr = policy.PartialResponse!;

        if (pr.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot(Bucket, policy.Id, requestKey, pr.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (pr.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (pr.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < pr.Probability)
        {
            fireReason = "probability";
            return true;
        }
        fireReason = string.Empty;
        return false;
    }

    private static void TagActivity(ActivePolicy policy, string fireReason, PartialResponseConfig pr)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.partial_response.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.partial_response.fire_reason", fireReason);
            activity.SetTag("chaos.proxy.partial_response.status", pr.Status);
            activity.SetTag("chaos.proxy.partial_response.written_bytes", pr.Body.Length);
            if (pr.AdvertisedContentLength.HasValue)
            {
                activity.SetTag("chaos.proxy.partial_response.advertised_bytes", pr.AdvertisedContentLength.Value);
            }
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.partial-response", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "partial-response");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
        fireActivity?.SetTag("chaos.proxy.partial_response.written_bytes", pr.Body.Length);
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
