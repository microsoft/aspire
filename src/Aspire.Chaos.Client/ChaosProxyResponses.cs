// <copyright file="ChaosProxyResponses.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Chaos.Client;

/// <summary>
/// Snapshot of an installed chaos policy returned by <see cref="ChaosProxyClient.ListPoliciesAsync"/>,
/// <see cref="ChaosProxyClient.GetPolicyAsync"/>, and <see cref="ChaosProxyClient.PreviewPolicyAsync"/>.
/// Mirrors the canonical policy shape with TTL + fire counters attached.
/// </summary>
public sealed record ChaosPolicySummary
{
    /// <summary>Unique policy id (server-assigned GUID if the install didn't supply one).</summary>
    public required string Id { get; init; }

    /// <summary>Request matcher. Null = matches every (non-/chaos/*) request.</summary>
    public ChaosMatcher? Matcher { get; init; }

    /// <summary>Latency transform config (null if this policy doesn't inject latency).</summary>
    public ChaosLatency? Latency { get; init; }

    /// <summary>Error transform config.</summary>
    public ChaosError? Error { get; init; }

    /// <summary>Replay-duplicate transform config.</summary>
    public ChaosReplayDuplicate? ReplayDuplicate { get; init; }

    /// <summary>Drop-response transform config.</summary>
    public ChaosDropResponse? DropResponse { get; init; }

    /// <summary>Rate-limit transform config.</summary>
    public ChaosRateLimit? RateLimit { get; init; }

    /// <summary>Header-tamper transform config.</summary>
    public ChaosHeaderTamper? HeaderTamper { get; init; }

    /// <summary>Partial-response transform config.</summary>
    public ChaosPartialResponse? PartialResponse { get; init; }

    /// <summary>Idempotency-key-collision transform config.</summary>
    public ChaosIdempotencyKeyCollision? IdempotencyCollision { get; init; }

    /// <summary>Slow-response transform config.</summary>
    public ChaosSlowResponse? SlowResponse { get; init; }

    /// <summary>Forward-then-fail transform config.</summary>
    public ChaosForwardThenFail? ForwardThenFail { get; init; }

    /// <summary>Wall-clock expiration. Null = lives forever (bootstrap policies).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Per-transform fire counters. Each key is a transform name ("latency", "error",
    /// "rate-limit", etc) and the value is how many times that transform has fired for
    /// this policy. Use to assert "did the chaos actually happen?" in harness tests.
    /// </summary>
    public IReadOnlyDictionary<string, long>? FireCounts { get; init; }
}

/// <summary>
/// Snapshot returned by <see cref="ChaosProxyClient.GetStateAsync"/>. Aggregates the
/// proxy's global flags + cross-policy counters into one round-trip for harness probes.
/// </summary>
public sealed record ChaosProxyState
{
    /// <summary>True if the proxy is currently in the paused state (no transforms fire).</summary>
    public bool Paused { get; init; }

    /// <summary>Number of currently-installed (non-expired) policies.</summary>
    public int PolicyCount { get; init; }

    /// <summary>Sum of every transform's fire counter across every policy.</summary>
    public long TotalFireCount { get; init; }

    /// <summary>Per-transform totals aggregated across all policies. Key = transform name.</summary>
    public IReadOnlyDictionary<string, long>? FireCountsByTransform { get; init; }

    /// <summary>
    /// Currently-armed fire-once triggers as wire keys. Global triggers are the bare
    /// transform name (e.g., "latency"); per-policy triggers are composite ("policyId:latency").
    /// </summary>
    public IReadOnlyList<string>? ArmedFireOnceTriggers { get; init; }
}

/// <summary>
/// One entry in the response from <see cref="ChaosProxyClient.MatchAsync"/> -
/// a policy whose matcher would fire on the hypothetical request, plus the list
/// of transforms that would actually run.
/// </summary>
public sealed record ChaosMatchEntry
{
    /// <summary>Id of the policy that matched.</summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Transforms that would fire (probability gates pass, fail-first budget has remaining
    /// quota, fire-once trigger armed, etc). Per D12 first-installed-wins per transform
    /// type, harnesses can filter for the FIRST match containing a transform.
    /// </summary>
    public required IReadOnlyList<string> TransformsThatWouldFire { get; init; }
}
