// <copyright file="ChaosRandomFaultMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Policy.Profiles;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Resource-aware random chaos. For the first matching policy carrying a
/// <see cref="RandomFaultConfig"/>, rolls the policy's intensity against its seeded RNG;
/// on a hit it samples one fault from the resource-type's <c>FaultProfile</c> (weighted,
/// seeded) and applies it — an error short-circuit, a latency delay, or a dropped
/// response. Seeded so a failing feature-resilience run is reproducible (D21). Runs early
/// in the pipeline (right after body buffering) so a sampled fault pre-empts the
/// deterministic transforms.
/// </summary>
internal sealed class ChaosRandomFaultMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly FaultProfileRegistry _profiles;
    private readonly ILogger<ChaosRandomFaultMiddleware> _logger;

    public ChaosRandomFaultMiddleware(
        RequestDelegate next,
        ActivePolicyStore store,
        FaultProfileRegistry profiles,
        ILogger<ChaosRandomFaultMiddleware> logger)
    {
        _next = next;
        _store = store;
        _profiles = profiles;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/chaos") || _store.IsPaused)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policy = FindMatchingPolicyWithRandom(context.Request);
        if (policy is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var cfg = policy.RandomFault!;

        // Safety rail: never fault excluded paths (health/readiness/startup, etc.).
        if (IsExcluded(context.Request, cfg.ExcludePaths))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Safety rail: global blast-radius cap across all request keys.
        if (cfg.MaxFires.HasValue && _store.GetFireCount(policy.Id, "random") >= cfg.MaxFires.Value)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var profile = _profiles.Resolve(cfg.ProfileId);

        // Roll intensity + sample atomically against the policy's seeded RNG. The intensity
        // roll consumes exactly one draw; the entry/param draws happen only on a hit, so a
        // miss doesn't desync the sequence relative to a different intensity.
        var sampled = _store.WithPolicyRandom(policy.Id, cfg.Seed, rng =>
            rng.NextDouble() < cfg.Intensity ? FaultProfileSampler.Sample(profile, rng) : null);

        if (sampled is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _store.RecordFire(policy.Id, "random", $"{context.Request.Method} {context.Request.Path}");
        ChaosMeter.RecordFire(policy.Id, "random", "intensity");
        TagActivity(policy, cfg, sampled);

        var frozenKind = sampled.Kind switch
        {
            SampledFaultKind.Error => "error",
            SampledFaultKind.Latency => "latency",
            SampledFaultKind.DropResponse => "drop",
            _ => "error",
        };
        _store.RecordFrozenFault(new FrozenFault(
            policy.Id,
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            frozenKind,
            sampled.Error?.Status,
            sampled.Latency?.MinMs));

        switch (sampled.Kind)
        {
            case SampledFaultKind.Error:
                await ApplyErrorAsync(context, sampled.Error!, policy.Id, profile.Id).ConfigureAwait(false);
                return;

            case SampledFaultKind.Latency:
                var delayMs = sampled.Latency!.MinMs;
                _logger.LogDebug(
                    "Random chaos: {DelayMs}ms latency on {Method} {Path} via policy {PolicyId} (profile {Profile})",
                    delayMs, context.Request.Method, context.Request.Path, policy.Id, profile.Id);
                await Task.Delay(delayMs, context.RequestAborted).ConfigureAwait(false);
                await _next(context).ConfigureAwait(false);
                return;

            case SampledFaultKind.DropResponse:
                _logger.LogDebug(
                    "Random chaos: dropping response on {Method} {Path} via policy {PolicyId} (profile {Profile})",
                    context.Request.Method, context.Request.Path, policy.Id, profile.Id);
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Expected: client gave up. Swallow so the pipeline doesn't 500.
                }

                return;

            default:
                await _next(context).ConfigureAwait(false);
                return;
        }
    }

    private async Task ApplyErrorAsync(HttpContext context, ErrorConfig err, string policyId, string profileId)
    {
        _logger.LogDebug(
            "Random chaos: HTTP {Status} on {Method} {Path} via policy {PolicyId} (profile {Profile})",
            err.Status, context.Request.Method, context.Request.Path, policyId, profileId);

        context.Response.StatusCode = err.Status;
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
    }

    private ActivePolicy? FindMatchingPolicyWithRandom(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.RandomFault is null)
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

    private static bool IsExcluded(HttpRequest request, IReadOnlyList<string>? excludePaths)
    {
        if (excludePaths is null || excludePaths.Count == 0)
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        foreach (var prefix in excludePaths)
        {
            if (!string.IsNullOrEmpty(prefix) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TagActivity(ActivePolicy policy, RandomFaultConfig cfg, SampledFault sampled)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetTag("chaos.proxy.fired", true);
            activity.SetTag("chaos.proxy.random.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.random.profile", cfg.ProfileId);
            activity.SetTag("chaos.proxy.random.entry_index", sampled.EntryIndex);
            activity.SetTag("chaos.proxy.random.fault_kind", sampled.Kind.ToString());
            if (sampled.Error is not null)
            {
                activity.SetTag("chaos.proxy.random.status", sampled.Error.Status);
            }
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.random", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "random");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.random.profile", cfg.ProfileId);
        fireActivity?.SetTag("chaos.proxy.random.entry_index", sampled.EntryIndex);
        fireActivity?.SetTag("chaos.proxy.random.fault_kind", sampled.Kind.ToString());
    }
}
