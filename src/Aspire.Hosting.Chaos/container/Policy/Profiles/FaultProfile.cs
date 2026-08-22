// <copyright file="FaultProfile.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace ChaosProxy.Container.Policy.Profiles;

/// <summary>
/// A named, weighted catalog of the faults that are plausible and reasonable for a given
/// resource type (e.g. <c>service.http</c>, <c>azure.cosmos</c>). A random chaos policy
/// references a profile by <see cref="Id"/>; the runtime samples one <see cref="FaultProfileEntry"/>
/// per firing request (weighted, seeded) and materializes it into one of the existing
/// transform configs. Profiles are data (embedded JSON resources), so they're tunable
/// without recompiling.
/// </summary>
internal sealed record FaultProfile
{
    /// <summary>Stable identifier, e.g. <c>service.http</c> or <c>azure.cosmos</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The weighted set of faults this profile can produce. Must be non-empty.</summary>
    public required IReadOnlyList<FaultProfileEntry> Entries { get; init; }

    /// <summary>
    /// Upper bound on how many consecutive times random chaos may fire on a single
    /// logical request key for this resource type before it must let one through, so a
    /// random fault stream can't exceed the dependency SDK's retry budget and wedge the
    /// caller permanently. Null = no resource-specific cap (use the global safety rail).
    /// E.g. Cosmos default retry policy retries 9x, so a safe value is 8.
    /// </summary>
    public int? SafeFailFirstMax { get; init; }
}

/// <summary>
/// One realistic fault a profile can produce, with a sampling <see cref="Weight"/>. The
/// <see cref="Kind"/> selects which transform primitive it resolves to; the remaining
/// fields are the primitive's parameters. <see cref="ParamRanges"/> lets a single entry
/// vary realistically: each named range is sampled per fire and substituted for the
/// <c>${name}</c> token wherever it appears in <see cref="Headers"/> values or
/// <see cref="Body"/> (e.g. a sampled <c>retryAfterMs</c> in the <c>x-ms-retry-after-ms</c>
/// header).
/// </summary>
internal sealed record FaultProfileEntry
{
    /// <summary>Relative sampling weight. Need not sum to 1 across entries; the sampler normalizes.</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>Transform primitive: <c>error</c>, <c>latency</c>, or <c>dropResponse</c> (case-insensitive).</summary>
    public required string Kind { get; init; }

    // --- error ---

    /// <summary>HTTP status for an <c>error</c> entry.</summary>
    public int? Status { get; init; }

    /// <summary>Optional response body for an <c>error</c> entry. May contain <c>${name}</c> tokens.</summary>
    public string? Body { get; init; }

    /// <summary>Optional content type for an <c>error</c> entry.</summary>
    public string? ContentType { get; init; }

    /// <summary>Optional response headers for an <c>error</c> entry. Values may contain <c>${name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    // --- latency ---

    /// <summary>Minimum delay (ms) for a <c>latency</c> entry. The sampler picks a value in [MinMs, MaxMs].</summary>
    public int? MinMs { get; init; }

    /// <summary>Maximum delay (ms) for a <c>latency</c> entry.</summary>
    public int? MaxMs { get; init; }

    // --- shared ---

    /// <summary>
    /// Named integer ranges (<c>name -&gt; [min, max]</c>) sampled per fire and substituted
    /// for <c>${name}</c> tokens in <see cref="Headers"/> values and <see cref="Body"/>.
    /// </summary>
    public IReadOnlyDictionary<string, int[]>? ParamRanges { get; init; }
}

/// <summary>The transform primitive a sampled fault resolved to.</summary>
internal enum SampledFaultKind
{
    Error,
    Latency,
    DropResponse,
}

/// <summary>
/// The concrete fault produced by sampling a <see cref="FaultProfile"/> for one request:
/// the chosen entry, its primitive kind, exactly one materialized transform config, and
/// the sampled parameter values (for telemetry / freeze-to-repro).
/// </summary>
internal sealed record SampledFault(
    int EntryIndex,
    SampledFaultKind Kind,
    ErrorConfig? Error,
    LatencyConfig? Latency,
    DropResponseConfig? DropResponse,
    IReadOnlyDictionary<string, int> SampledParams);
