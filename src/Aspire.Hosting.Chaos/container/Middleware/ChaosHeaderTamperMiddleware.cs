// <copyright file="ChaosHeaderTamperMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Tampers with request and/or response headers. Pipeline position: AFTER latency and
/// AFTER error (you can still tamper headers on errored responses if the policy targets
/// Response/Both - the error middleware writes its headers then returns; on response
/// the OnStarting callback fires before the body so we can adjust headers in-flight)
/// and BEFORE rate-limit/drop/replay so request-side tampering reaches all downstream
/// stages plus the upstream.
/// </summary>
internal sealed class ChaosHeaderTamperMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosHeaderTamperMiddleware> _logger;

    public ChaosHeaderTamperMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosHeaderTamperMiddleware> logger)
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

        var policy = FindMatchingPolicyWithHeaderTamper(context.Request);
        if (policy is not null)
        {
            var tamper = policy.HeaderTamper!;
            var appliesToRequest = tamper.Direction is HeaderTamperDirection.Request or HeaderTamperDirection.Both;
            var appliesToResponse = tamper.Direction is HeaderTamperDirection.Response or HeaderTamperDirection.Both;

            if (appliesToRequest)
            {
                ApplyToHeaders(context.Request.Headers, tamper);
                TagActivity(policy, tamper, "request");
                _store.RecordFire(policy.Id, "header-tamper");
                ChaosMeter.RecordFire(policy.Id, "header-tamper", "request");
            }

            if (appliesToResponse)
            {
                // OnStarting fires AFTER downstream pipeline runs but BEFORE the
                // response body is written, which is the only safe window to mutate
                // response headers. Captures the policy/tamper for the closure.
                var policyCapture = policy;
                var tamperCapture = tamper;
                var storeCapture = _store;
                context.Response.OnStarting(state =>
                {
                    var ctx = (HttpContext)state;
                    ApplyToHeaders(ctx.Response.Headers, tamperCapture);
                    TagActivity(policyCapture, tamperCapture, "response");
                    storeCapture.RecordFire(policyCapture.Id, "header-tamper");
                    ChaosMeter.RecordFire(policyCapture.Id, "header-tamper", "response");
                    return Task.CompletedTask;
                }, context);
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private ActivePolicy? FindMatchingPolicyWithHeaderTamper(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.HeaderTamper is null)
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

    private static void ApplyToHeaders(IHeaderDictionary headers, HeaderTamperConfig tamper)
    {
        if (tamper.Remove is not null)
        {
            foreach (var name in tamper.Remove)
            {
                headers.Remove(name);
            }
        }

        if (tamper.Set is not null)
        {
            foreach (var kv in tamper.Set)
            {
                headers[kv.Key] = kv.Value;
            }
        }

        if (tamper.Add is not null)
        {
            foreach (var kv in tamper.Add)
            {
                headers.Append(kv.Key, kv.Value);
            }
        }
    }

    private static void TagActivity(ActivePolicy policy, HeaderTamperConfig tamper, string appliedTo)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag($"chaos.proxy.header_tamper.{appliedTo}.policy_id", policy.Id);
            activity.SetTag($"chaos.proxy.header_tamper.{appliedTo}.direction", tamper.Direction.ToString());
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity($"chaos.header-tamper.{appliedTo}", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "header-tamper");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.header_tamper.applied_to", appliedTo);
        fireActivity?.SetTag("chaos.proxy.header_tamper.direction", tamper.Direction.ToString());
    }
}
