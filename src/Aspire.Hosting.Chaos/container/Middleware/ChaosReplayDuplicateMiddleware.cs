// <copyright file="ChaosReplayDuplicateMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Middleware that, when fires, lets the primary request through normally AND schedules
/// a fire-and-forget background HTTP call duplicating the same request to the upstream.
/// Iterates the <see cref="ActivePolicyStore"/> and applies the first matching policy's
/// ReplayDuplicateConfig (per D12, first-installed-wins on overlap per transform type).
/// </summary>
internal sealed class ChaosReplayDuplicateMiddleware
{
    private const string ClientRequestIdHeader = "x-ms-client-request-id";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    // Hop-by-hop headers that must not be forwarded per RFC 7230 §6.1.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
    };

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChaosReplayDuplicateMiddleware> _logger;
    private readonly string? _upstreamUrl;

    public ChaosReplayDuplicateMiddleware(
        RequestDelegate next,
        ActivePolicyStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<ChaosReplayDuplicateMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _upstreamUrl = configuration["ReverseProxy:Clusters:c1:Destinations:d1:Address"];
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

        ReplayContext? captured = null;
        var policy = FindMatchingPolicyWithReplay(context.Request);
        if (policy is not null && _upstreamUrl is not null && TryFire(context, policy, out var fireReason))
        {
            TagActivity(policy, fireReason);
            _store.RecordFire(policy.Id, "replay-duplicate");
            ChaosMeter.RecordFire(policy.Id, "replay-duplicate", fireReason);
            captured = await CaptureRequestAsync(context, policy.Id).ConfigureAwait(false);
        }

        await _next(context).ConfigureAwait(false);

        if (captured is not null)
        {
            var captureForBackground = captured;
            _ = Task.Run(() => ReplayAsync(captureForBackground));
        }
    }

    private ActivePolicy? FindMatchingPolicyWithReplay(HttpRequest request)
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.ReplayDuplicate is null)
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
        if (_store.ConsumeFireOnceForPolicy(policy.Id, "replay-duplicate"))
        {
            fireReason = "fire-once";
            return true;
        }
        if (_store.ConsumeFireOnce("replay-duplicate"))
        {
            fireReason = "fire-once";
            return true;
        }

        var rd = policy.ReplayDuplicate!;

        if (rd.FailFirst.HasValue)
        {
            var requestKey = DeriveRequestKey(context);
            if (_store.ConsumeFailFirstSlot("replay-duplicate", policy.Id, requestKey, rd.FailFirst.Value))
            {
                fireReason = "fail-first";
                return true;
            }
            fireReason = string.Empty;
            return false;
        }

        if (rd.Probability >= 1.0)
        {
            fireReason = "probability";
            return true;
        }
        if (rd.Probability <= 0.0)
        {
            fireReason = string.Empty;
            return false;
        }

        if (Random.Shared.NextDouble() < rd.Probability)
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
            activity.SetTag("chaos.proxy.replay.policy_id", policy.Id);
            activity.SetTag("chaos.proxy.replay.fire_reason", fireReason);
            activity.SetTag("chaos.proxy.replay.scheduled", true);
        }

        using var fireActivity = ChaosActivitySource.Source.StartActivity("chaos.replay-duplicate", ActivityKind.Internal);
        fireActivity?.SetTag("chaos.proxy.transform", "replay-duplicate");
        fireActivity?.SetTag("chaos.proxy.policy_id", policy.Id);
        fireActivity?.SetTag("chaos.proxy.fire_reason", fireReason);
    }

    private static async Task<ReplayContext> CaptureRequestAsync(HttpContext context, string policyId)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }
            headers[header.Key] = header.Value.ToArray()!;
        }

        context.Request.EnableBuffering();
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(ms, context.RequestAborted).ConfigureAwait(false);
            body = ms.ToArray();
        }
        context.Request.Body.Position = 0;

        return new ReplayContext(
            PolicyId: policyId,
            Method: context.Request.Method,
            PathAndQuery: context.Request.Path + context.Request.QueryString,
            Headers: headers,
            Body: body);
    }

    private async Task ReplayAsync(ReplayContext captured)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            var url = _upstreamUrl!.TrimEnd('/') + captured.PathAndQuery;
            using var req = new HttpRequestMessage(new HttpMethod(captured.Method), url);

            if (captured.Body.Length > 0)
            {
                req.Content = new ByteArrayContent(captured.Body);
            }

            foreach (var kv in captured.Headers)
            {
                if (!req.Headers.TryAddWithoutValidation(kv.Key, kv.Value) && req.Content is not null)
                {
                    req.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }

            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            _logger.LogInformation(
                "Replay-duplicate fired: {Method} {Path} -> {Status} via policy {PolicyId}",
                captured.Method, captured.PathAndQuery, (int)resp.StatusCode, captured.PolicyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Replay-duplicate failed for {Method} {Path} via policy {PolicyId}",
                captured.Method, captured.PathAndQuery, captured.PolicyId);
        }
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

    private sealed record ReplayContext(string PolicyId, string Method, string PathAndQuery, Dictionary<string, string[]> Headers, byte[] Body);
}
