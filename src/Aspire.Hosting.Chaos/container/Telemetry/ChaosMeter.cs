// <copyright file="ChaosMeter.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.Metrics;

namespace ChaosProxy.Container.Telemetry;

/// <summary>
/// OpenTelemetry Meter that emits chaos-proxy metrics for the Aspire dashboard's
/// Metrics tab. Complements the trace-level <see cref="ChaosActivitySource"/> tags
/// (which describe individual request events) with aggregate counters + gauges
/// (which describe the chaos proxy's state across time):
///
/// <list type="bullet">
///   <item><description><c>chaos.proxy.fires</c> — Counter&lt;long&gt;: incremented each
///   time a transform fires, tagged with <c>policy_id</c>, <c>transform</c>, and
///   <c>fire_reason</c> (probability / fail-first / fire-once / rate-exceeded).
///   Use case: <c>rate(chaos.proxy.fires{transform="error"}[1m])</c> alerts.</description></item>
///   <item><description><c>chaos.proxy.policies.active</c> — ObservableGauge&lt;long&gt;:
///   the count of currently-installed (non-expired) policies. Use case: dashboard
///   widget showing "is anything chaos-y configured right now?".</description></item>
/// </list>
///
/// The Aspire dashboard auto-discovers any Meter wired via <c>AddMeter(...)</c>
/// during OpenTelemetry setup and surfaces it under the resource's Metrics tab.
/// </summary>
internal static class ChaosMeter
{
    public const string Name = "Aspire.Hosting.Chaos";

    public static readonly Meter Source = new(Name);

    /// <summary>
    /// Counter incremented once per transform fire. Tags:
    /// <c>policy_id</c> (the installing harness's id), <c>transform</c> (latency / error /
    /// rate-limit / etc), <c>fire_reason</c> (probability / fail-first / fire-once /
    /// rate-exceeded). Aggregated across all middleware so cardinality stays bounded by
    /// (policies installed) × 9 transforms × 4 fire reasons.
    /// </summary>
    public static readonly Counter<long> Fires = Source.CreateCounter<long>(
        "chaos.proxy.fires",
        unit: "{fire}",
        description: "Number of times a chaos transform fired. Tagged with policy_id, transform, fire_reason.");

    /// <summary>
    /// Helper to centralize the tag list shape so every callsite emits the same tag schema.
    /// </summary>
    public static void RecordFire(string policyId, string transform, string fireReason)
    {
        Fires.Add(1,
            new KeyValuePair<string, object?>("policy_id", policyId),
            new KeyValuePair<string, object?>("transform", transform),
            new KeyValuePair<string, object?>("fire_reason", fireReason));
    }
}
