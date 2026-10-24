// <copyright file="ChaosEndpoints.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace ChaosProxy.Container;

/// <summary>
/// Maps the chaos proxy's HTTP control-plane endpoints onto the supplied
/// <see cref="IEndpointRouteBuilder"/>. Production wiring lives in <c>Program.cs</c>;
/// tests use this directly via TestServer to exercise the endpoint contracts without
/// spinning up YARP, OpenTelemetry, or the chaos middleware pipeline.
/// </summary>
internal static class ChaosEndpoints
{
    public static void MapChaosEndpoints(this IEndpointRouteBuilder endpoints, ILogger? logger = null)
    {
        // /chaos/* paths are handled locally - never delayed/errored/replayed (the middleware
        // pipeline also early-returns for /chaos/* as defense in depth).
        endpoints.MapGet("/chaos/healthz", () => Results.Ok(new { status = "healthy" }));

        // Runtime policy API - lets a harness install / list / remove chaos policies on a
        // running AppHost without rebuilding. Per D8, conductor's install_chaos_policy /
        // teardown_chaos_policy workflow steps drive this in production.
        endpoints.MapGet("/chaos/policies", (ActivePolicyStore s) =>
        {
            var summaries = s.GetActive().Select(p => ToPolicySummary(p, s.GetFireCounts(p.Id))).ToList();
            return Results.Ok(new PolicyListResponse(summaries));
        });

        // Per-policy GET: focused single-policy view with its fire counts. Saves bandwidth
        // over filtering the full list client-side and makes harness assertions clearer
        // ("get this policy" vs "get all and find").
        endpoints.MapGet("/chaos/policies/{id}", (string id, ActivePolicyStore s) =>
        {
            var policy = s.TryGet(id);
            if (policy is null)
            {
                return Results.NotFound(new { error = $"policy {id} not found" });
            }
            return Results.Ok(ToPolicySummary(policy, s.GetFireCounts(policy.Id)));
        });

        // Per-policy fire-counts GET: the smallest assertion target. Returns the same
        // shape that lands inline on the full policy view, so the harness can probe just
        // the dynamic state without re-reading the static policy config.
        endpoints.MapGet("/chaos/policies/{id}/fire-counts", (string id, ActivePolicyStore s) =>
        {
            // A policy whose TTL has lapsed is swept from the active set, but its fire
            // counters are retained (SweepExpired does not clear them). A long-running test
            // arms a short-TTL fault that must expire before a downstream recovery runs, then
            // asserts fire counts AFTER the wait — by which point the policy is swept. Return
            // the retained tally for any policy that is active OR has a recorded fire; 404
            // only for a genuinely-unknown id.
            if (s.TryGet(id) is null && !s.HasFireRecord(id))
            {
                return Results.NotFound(new { error = $"policy {id} not found" });
            }
            var counts = s.GetFireCounts(id);
            return Results.Ok(new { id, fireCounts = counts, firedPaths = s.GetFiredPaths(id) });
        });

        // Extend TTL: sets a fresh expiry from now on the existing policy. Lets long-
        // running tests keep their chaos policy alive without removing + reinstalling.
        // seconds=0 clears the expiry entirely (policy lives until explicitly removed).
        endpoints.MapPost("/chaos/policies/{id}/extend", (string id, int seconds, ActivePolicyStore s) =>
        {
            if (seconds < 0)
            {
                return Results.BadRequest(new { error = "seconds must be >= 0 (0 means clear expiry entirely)" });
            }
            var extended = s.ExtendTtl(id, TimeSpan.FromSeconds(seconds));
            if (!extended)
            {
                return Results.NotFound(new { error = $"policy {id} not found or already expired" });
            }
            logger?.LogInformation("Extended TTL on chaos policy {PolicyId} by {Seconds}s", id, seconds);
            var expiresAt = s.TryGet(id)?.ExpiresAt;
            return Results.Ok(new { id, expiresAt });
        });

        endpoints.MapPost("/chaos/policies", (InstallPolicyRequest req, ActivePolicyStore s) =>
        {
            if (req is null)
            {
                return Results.BadRequest(new { error = "request body required" });
            }

            var result = InstallSinglePolicy(req, s);
            if (result.Error is not null)
            {
                return Results.BadRequest(new { error = result.Error });
            }
            logger?.LogInformation("Installed chaos policy {PolicyId} (expires {ExpiresAt})", result.Id, result.ExpiresAt);
            return Results.Ok(new InstallPolicyResponse(result.Id!));
        });

        // Bulk install: POST /chaos/policies/bulk with a JSON array of InstallPolicyRequest.
        // Atomic from the harness's perspective: either all policies install, or none do
        // (validation runs across the whole batch before any are added to the store). Useful
        // for harnesses that need to set up multi-policy scenarios in one round-trip.
        endpoints.MapPost("/chaos/policies/bulk", (List<InstallPolicyRequest> requests, ActivePolicyStore s) =>
        {
            if (requests is null || requests.Count == 0)
            {
                return Results.BadRequest(new { error = "request body must be a non-empty array of policy install requests" });
            }

            var ids = new List<string>(requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                var (id, _, error) = ValidateAndComputeId(requests[i]);
                if (error is not null)
                {
                    return Results.BadRequest(new { error = $"policy[{i}]: {error}" });
                }
                ids.Add(id!);
            }

            for (var i = 0; i < requests.Count; i++)
            {
                _ = InstallSinglePolicy(requests[i] with { Id = ids[i] }, s);
            }

            logger?.LogInformation("Installed {Count} chaos policies via bulk", ids.Count);
            return Results.Ok(new { installed = ids.Count, ids });
        });

        // Preview: validate a policy + return the canonical shape it WOULD take (defaults
        // applied, TTL resolved, generated id if none supplied) WITHOUT installing. Lets
        // harnesses pre-flight a policy before committing.
        endpoints.MapPost("/chaos/preview-policy", (InstallPolicyRequest req) =>
        {
            if (req is null)
            {
                return Results.BadRequest(new { error = "request body required" });
            }

            var (id, expiresAt, error) = ValidateAndComputeId(req);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            var built = BuildActivePolicy(req, id!, expiresAt);
            return Results.Ok(ToPolicySummary(built, new Dictionary<string, long>()));
        });

        endpoints.MapDelete("/chaos/policies/{id}", (string id, ActivePolicyStore s) =>
        {
            var removed = s.Remove(id);
            if (!removed)
            {
                return Results.NotFound(new { error = $"policy {id} not found" });
            }
            logger?.LogInformation("Removed chaos policy {PolicyId}", id);
            return Results.NoContent();
        });

        // Bulk clear: DELETE /chaos/policies (no id) wipes every installed policy plus
        // resets all chaos state (fire counters, failFirst counters, rate-limit windows,
        // idempotency-key cache, fire-once triggers). Pause flag is preserved.
        endpoints.MapDelete("/chaos/policies", (ActivePolicyStore s) =>
        {
            var removed = s.Clear();
            logger?.LogInformation("Cleared all chaos policies ({Removed} removed) and reset all chaos state", removed);
            return Results.Ok(new { removed });
        });

        // Pause / resume: global "all transforms off" toggle. Driven by the pause-faults /
        // resume-faults dashboard commands or by direct HTTP POST. Idempotent both directions.
        endpoints.MapPost("/chaos/pause", (ActivePolicyStore s) =>
        {
            s.Pause();
            logger?.LogInformation("Chaos transforms paused");
            return Results.NoContent();
        });

        endpoints.MapPost("/chaos/resume", (ActivePolicyStore s) =>
        {
            s.Resume();
            logger?.LogInformation("Chaos transforms resumed");
            return Results.NoContent();
        });

        // Enriched state probe for harness assertions.
        endpoints.MapGet("/chaos/state", (ActivePolicyStore s) =>
        {
            var fireCounts = s.GetAllFireCounts();
            return Results.Ok(new
            {
                paused = s.IsPaused,
                policyCount = s.GetActive().Count,
                totalFireCount = fireCounts.Values.Sum(),
                fireCountsByTransform = fireCounts,
                armedFireOnceTriggers = s.GetArmedFireOnceTriggers(),
            });
        });

        // Global fire-once: arms a trigger that the corresponding middleware consumes on
        // the NEXT matching request, firing the transform regardless of normal probability /
        // failFirst gates.
        endpoints.MapPost("/chaos/fire-once", (string transform, ActivePolicyStore s) =>
        {
            if (!IsValidTransform(transform))
            {
                return Results.BadRequest(new { error = ValidTransformsError });
            }

            s.SetFireOnce(transform);
            logger?.LogInformation("Armed fire-once trigger for transform {Transform}", transform);
            return Results.NoContent();
        });

        // Per-policy fire-once.
        endpoints.MapPost("/chaos/policies/{id}/fire-once", (string id, string transform, ActivePolicyStore s) =>
        {
            if (!IsValidTransform(transform))
            {
                return Results.BadRequest(new { error = ValidTransformsError });
            }

            if (!s.GetActive().Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
            {
                return Results.NotFound(new { error = $"policy {id} not found" });
            }

            s.SetFireOnceForPolicy(id, transform);
            logger?.LogInformation("Armed per-policy fire-once trigger: policy={PolicyId} transform={Transform}", id, transform);
            return Results.NoContent();
        });

        // Per-policy counter reset.
        endpoints.MapDelete("/chaos/policies/{id}/fire-counts", (string id, ActivePolicyStore s) =>
        {
            if (!s.GetActive().Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
            {
                return Results.NotFound(new { error = $"policy {id} not found" });
            }

            s.ResetFireCounts(id);
            logger?.LogInformation("Reset fire counters for policy {PolicyId}", id);
            return Results.NoContent();
        });

        // Match prediction: synthesize a hypothetical request and report which active
        // policies would match it + which transforms each would fire. Pure debugging
        // tool - no side effects, no probability rolls, no failFirst consumption. Lets
        // harnesses (and humans) answer 'what would happen if I sent THIS request?'
        // without actually sending it.
        endpoints.MapPost("/chaos/match", (MatchPredictionRequest req, ActivePolicyStore s) =>
        {
            if (req is null || string.IsNullOrEmpty(req.Path))
            {
                return Results.BadRequest(new { error = "request body with 'path' field required" });
            }

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = string.IsNullOrEmpty(req.Method) ? "GET" : req.Method;
            ctx.Request.Path = new PathString(req.Path.StartsWith('/') ? req.Path : "/" + req.Path);
            if (req.Headers is not null)
            {
                foreach (var kv in req.Headers)
                {
                    ctx.Request.Headers[kv.Key] = kv.Value;
                }
            }

            var matches = new List<MatchPredictionEntry>();
            foreach (var policy in s.GetActive())
            {
                if (policy.Matcher is not null && !policy.Matcher.Matches(ctx.Request))
                {
                    continue;
                }

                var transforms = new List<string>();
                if (policy.Latency is not null) transforms.Add("latency");
                if (policy.HeaderTamper is not null) transforms.Add("header-tamper");
                if (policy.IdempotencyCollision is not null) transforms.Add("idempotency-collision");
                if (policy.Error is not null) transforms.Add("error");
                if (policy.RateLimit is not null) transforms.Add("rate-limit");
                if (policy.PartialResponse is not null) transforms.Add("partial-response");
                if (policy.SlowResponse is not null) transforms.Add("slow-response");
                if (policy.DropResponse is not null) transforms.Add("drop-response");
                if (policy.ReplayDuplicate is not null) transforms.Add("replay-duplicate");
                if (policy.ForwardThenFail is not null) transforms.Add("forward-then-fail");
                if (policy.RandomFault is not null) transforms.Add("random");

                matches.Add(new MatchPredictionEntry(policy.Id, transforms));
            }

            return Results.Ok(new MatchPredictionResponse(matches));
        });

        // Freeze: convert the random-chaos fired-fault log into a deterministic
        // chaos_policies[] block that reproduces exactly what fired. Bridges exploratory
        // random chaos (which broke a feature) into a checked-in-able repro for the fix loop.
        endpoints.MapPost("/chaos/freeze", (ActivePolicyStore s) =>
            Results.Ok(new FreezeResponse(BuildFreezePolicies(s.GetFrozenFaults()))));
    }

    /// <summary>
    /// Collapses the random-chaos fired-fault log into a deduplicated list of deterministic
    /// install requests: one policy per distinct (method, path, fault) tuple, each scoped to
    /// that request and shaped to fire once (failFirst/maxFires = 1) so a replay reproduces
    /// the same faults without the randomness.
    /// </summary>
    internal static IReadOnlyList<InstallPolicyRequest> BuildFreezePolicies(IReadOnlyList<FrozenFault> faults)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var policies = new List<InstallPolicyRequest>();
        var index = 0;

        foreach (var fault in faults)
        {
            var dedupKey = $"{fault.Method}|{fault.Path}|{fault.Kind}|{fault.Status}|{fault.DelayMs}";
            if (!seen.Add(dedupKey))
            {
                continue;
            }

            var matcher = new MatcherDto(fault.Method, fault.Path, null, null, null);
            var id = $"frozen-{index++}";

            var request = fault.Kind switch
            {
                "error" => new InstallPolicyRequest(
                    Id: id, Matcher: matcher,
                    Latency: null,
                    Error: new ErrorDto(fault.Status ?? 500, null, null, null, Probability: null, FailFirst: 1),
                    ReplayDuplicate: null, DropResponse: null, RateLimit: null, HeaderTamper: null,
                    PartialResponse: null, IdempotencyCollision: null, SlowResponse: null, TtlSeconds: 300),

                "latency" => new InstallPolicyRequest(
                    Id: id, Matcher: matcher,
                    Latency: new LatencyDto(fault.DelayMs ?? 0, fault.DelayMs ?? 0, Probability: null, FailFirst: 1),
                    Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null, HeaderTamper: null,
                    PartialResponse: null, IdempotencyCollision: null, SlowResponse: null, TtlSeconds: 300),

                "drop" => new InstallPolicyRequest(
                    Id: id, Matcher: matcher,
                    Latency: null, Error: null, ReplayDuplicate: null,
                    DropResponse: new DropResponseDto(Probability: null, FailFirst: 1, MaxFires: 1),
                    RateLimit: null, HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null,
                    SlowResponse: null, TtlSeconds: 300),

                _ => null,
            };

            if (request is not null)
            {
                policies.Add(request);
            }
        }

        return policies;
    }

    private const string ValidTransformsError = "transform must be one of: latency, error, replay-duplicate, drop-response, rate-limit, partial-response, slow-response, forward-then-fail";

    private static bool IsValidTransform(string? transform) => transform is "latency" or "error" or "replay-duplicate" or "drop-response" or "rate-limit" or "partial-response" or "slow-response" or "forward-then-fail" or "random";

    internal static (string? Id, DateTimeOffset? ExpiresAt, string? Error) ValidateAndComputeId(InstallPolicyRequest req)
    {
        if (req.Latency is null && req.Error is null && req.ReplayDuplicate is null && req.DropResponse is null && req.RateLimit is null && req.HeaderTamper is null && req.PartialResponse is null && req.IdempotencyCollision is null && req.SlowResponse is null && req.ForwardThenFail is null && req.RandomFault is null)
        {
            return (null, null, "at least one transform (latency, error, replayDuplicate, dropResponse, rateLimit, headerTamper, partialResponse, idempotencyCollision, slowResponse, forwardThenFail, randomFault) must be specified");
        }

        if (req.ForwardThenFail is { } ftf)
        {
            if (ftf.Status is { } status && (status < 100 || status > 599))
            {
                return (null, null, $"forwardThenFail.status must be a valid HTTP status code (100-599); got {status}");
            }
            if (ftf.UpstreamTimeoutSeconds is { } timeout && timeout <= 0)
            {
                return (null, null, $"forwardThenFail.upstreamTimeoutSeconds must be > 0; got {timeout}");
            }
            if (ftf.Probability is { } prob && (prob < 0.0 || prob > 1.0 || double.IsNaN(prob)))
            {
                return (null, null, $"forwardThenFail.probability must be in [0.0, 1.0]; got {prob}");
            }
            if (ftf.FailFirst is { } ff && ff < 0)
            {
                return (null, null, $"forwardThenFail.failFirst must be >= 0; got {ff}");
            }
            if (ftf.MaxFires is { } mf && mf < 0)
            {
                return (null, null, $"forwardThenFail.maxFires must be >= 0; got {mf}");
            }
        }

        var id = string.IsNullOrEmpty(req.Id) ? Guid.NewGuid().ToString("n") : req.Id;
        var ttlSeconds = req.TtlSeconds ?? 300;
        var expiresAt = ttlSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(ttlSeconds) : (DateTimeOffset?)null;
        return (id, expiresAt, null);
    }

    internal static (string? Id, DateTimeOffset? ExpiresAt, string? Error) InstallSinglePolicy(InstallPolicyRequest req, ActivePolicyStore s)
    {
        var (id, expiresAt, error) = ValidateAndComputeId(req);
        if (error is not null)
        {
            return (null, null, error);
        }

        var policy = BuildActivePolicy(req, id!, expiresAt);
        s.Add(policy);
        return (id, expiresAt, null);
    }

    internal static ActivePolicy BuildActivePolicy(InstallPolicyRequest req, string id, DateTimeOffset? expiresAt)
    {
        return new ActivePolicy(
            Id: id,
            Matcher: req.Matcher is null ? null : new RequestMatcher(req.Matcher.Method, req.Matcher.PathPrefix, req.Matcher.PathContains, req.Matcher.HeaderEquals, req.Matcher.HeaderContains, req.Matcher.BodyContains, req.Matcher.DtfxActivityName),
            Latency: req.Latency is null ? null : new LatencyConfig(req.Latency.MinMs, req.Latency.MaxMs, req.Latency.Probability ?? 1.0, req.Latency.FailFirst),
            Error: req.Error is null ? null : new ErrorConfig(req.Error.Status, req.Error.Body, req.Error.ContentType, req.Error.Headers, req.Error.Probability ?? 1.0, req.Error.FailFirst),
            ReplayDuplicate: req.ReplayDuplicate is null ? null : new ReplayDuplicateConfig(req.ReplayDuplicate.Probability ?? 1.0, req.ReplayDuplicate.FailFirst),
            DropResponse: req.DropResponse is null ? null : new DropResponseConfig(req.DropResponse.Probability ?? 1.0, req.DropResponse.FailFirst, req.DropResponse.MaxFires),
            RateLimit: req.RateLimit is null ? null : new RateLimitConfig(req.RateLimit.RequestsPerWindow, req.RateLimit.WindowMs, req.RateLimit.Status ?? 429, req.RateLimit.Headers),
            HeaderTamper: req.HeaderTamper is null ? null : new HeaderTamperConfig(
                Direction: Enum.TryParse<HeaderTamperDirection>(req.HeaderTamper.Direction, ignoreCase: true, out var dir) ? dir : HeaderTamperDirection.Both,
                Remove: req.HeaderTamper.Remove,
                Set: req.HeaderTamper.Set,
                Add: req.HeaderTamper.Add),
            PartialResponse: req.PartialResponse is null ? null : new PartialResponseConfig(
                Status: req.PartialResponse.Status ?? 200,
                ContentType: req.PartialResponse.ContentType,
                Body: string.IsNullOrEmpty(req.PartialResponse.Body) ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(req.PartialResponse.Body),
                AdvertisedContentLength: req.PartialResponse.AdvertisedContentLength,
                AbortAfterMs: req.PartialResponse.AbortAfterMs ?? 0,
                Probability: req.PartialResponse.Probability ?? 1.0,
                FailFirst: req.PartialResponse.FailFirst),
            IdempotencyCollision: req.IdempotencyCollision is null ? null : new IdempotencyCollisionConfig(
                KeyHeaderName: string.IsNullOrEmpty(req.IdempotencyCollision.KeyHeaderName) ? "Idempotency-Key" : req.IdempotencyCollision.KeyHeaderName,
                Status: req.IdempotencyCollision.Status ?? 409,
                Body: req.IdempotencyCollision.Body,
                ContentType: req.IdempotencyCollision.ContentType,
                Headers: req.IdempotencyCollision.Headers,
                WindowMs: req.IdempotencyCollision.WindowMs ?? 60_000),
            SlowResponse: req.SlowResponse is null ? null : new SlowResponseConfig(
                Status: req.SlowResponse.Status ?? 200,
                ContentType: req.SlowResponse.ContentType,
                Body: string.IsNullOrEmpty(req.SlowResponse.Body) ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(req.SlowResponse.Body),
                BytesPerSecond: req.SlowResponse.BytesPerSecond ?? 1024,
                ChunkSize: req.SlowResponse.ChunkSize ?? 64,
                Probability: req.SlowResponse.Probability ?? 1.0,
                FailFirst: req.SlowResponse.FailFirst),
            ExpiresAt: expiresAt,
            ForwardThenFail: req.ForwardThenFail is null ? null : new ForwardThenFailConfig(
                Status: req.ForwardThenFail.Status ?? 503,
                ContentType: req.ForwardThenFail.ContentType,
                Body: req.ForwardThenFail.Body,
                Headers: req.ForwardThenFail.Headers,
                UpstreamTimeoutSeconds: req.ForwardThenFail.UpstreamTimeoutSeconds ?? 30,
                Probability: req.ForwardThenFail.Probability ?? 1.0,
                FailFirst: req.ForwardThenFail.FailFirst,
                MaxFires: req.ForwardThenFail.MaxFires),
            RandomFault: req.RandomFault is null ? null : new RandomFaultConfig(
                ProfileId: string.IsNullOrEmpty(req.RandomFault.ProfileId) ? "service.http" : req.RandomFault.ProfileId,
                Intensity: req.RandomFault.Intensity ?? 0.1,
                Seed: req.RandomFault.Seed ?? Random.Shared.Next(),
                MaxFires: req.RandomFault.MaxFires,
                ExcludePaths: req.RandomFault.ExcludePaths));
    }

    internal static PolicySummaryDto ToPolicySummary(ActivePolicy p, IReadOnlyDictionary<string, long> fireCounts)
        => new(
            Id: p.Id,
            Matcher: p.Matcher is null ? null : new MatcherDto(p.Matcher.Method, p.Matcher.PathPrefix, p.Matcher.PathContains, p.Matcher.HeaderEquals, p.Matcher.HeaderContains, p.Matcher.BodyContains, p.Matcher.DtfxActivityName),
            Latency: p.Latency is null ? null : new LatencyDto(p.Latency.MinMs, p.Latency.MaxMs, p.Latency.Probability, p.Latency.FailFirst),
            Error: p.Error is null ? null : new ErrorDto(p.Error.Status, p.Error.Body, p.Error.ContentType, p.Error.Headers, p.Error.Probability, p.Error.FailFirst),
            ReplayDuplicate: p.ReplayDuplicate is null ? null : new ReplayDuplicateDto(p.ReplayDuplicate.Probability, p.ReplayDuplicate.FailFirst),
            DropResponse: p.DropResponse is null ? null : new DropResponseDto(p.DropResponse.Probability, p.DropResponse.FailFirst, p.DropResponse.MaxFires),
            RateLimit: p.RateLimit is null ? null : new RateLimitDto(p.RateLimit.RequestsPerWindow, p.RateLimit.WindowMs, p.RateLimit.Status, p.RateLimit.Headers),
            HeaderTamper: p.HeaderTamper is null ? null : new HeaderTamperDto(p.HeaderTamper.Direction.ToString(), p.HeaderTamper.Remove, p.HeaderTamper.Set, p.HeaderTamper.Add),
            PartialResponse: p.PartialResponse is null ? null : new PartialResponseDto(p.PartialResponse.Status, p.PartialResponse.ContentType, System.Text.Encoding.UTF8.GetString(p.PartialResponse.Body), p.PartialResponse.AdvertisedContentLength, p.PartialResponse.AbortAfterMs, p.PartialResponse.Probability, p.PartialResponse.FailFirst),
            IdempotencyCollision: p.IdempotencyCollision is null ? null : new IdempotencyCollisionDto(p.IdempotencyCollision.KeyHeaderName, p.IdempotencyCollision.Status, p.IdempotencyCollision.Body, p.IdempotencyCollision.ContentType, p.IdempotencyCollision.Headers, p.IdempotencyCollision.WindowMs),
            SlowResponse: p.SlowResponse is null ? null : new SlowResponseDto(p.SlowResponse.Status, p.SlowResponse.ContentType, System.Text.Encoding.UTF8.GetString(p.SlowResponse.Body), p.SlowResponse.BytesPerSecond, p.SlowResponse.ChunkSize, p.SlowResponse.Probability, p.SlowResponse.FailFirst),
            ExpiresAt: p.ExpiresAt,
            FireCounts: fireCounts.Count == 0 ? null : fireCounts,
            ForwardThenFail: p.ForwardThenFail is null ? null : new ForwardThenFailDto(p.ForwardThenFail.Status, p.ForwardThenFail.ContentType, p.ForwardThenFail.Body, p.ForwardThenFail.Headers, p.ForwardThenFail.UpstreamTimeoutSeconds, p.ForwardThenFail.Probability, p.ForwardThenFail.FailFirst, p.ForwardThenFail.MaxFires),
            RandomFault: p.RandomFault is null ? null : new RandomFaultDto(p.RandomFault.ProfileId, p.RandomFault.Intensity, p.RandomFault.Seed, p.RandomFault.MaxFires, p.RandomFault.ExcludePaths));
}
