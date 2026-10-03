// <copyright file="FaultProfileSampler.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Globalization;

namespace ChaosProxy.Container.Policy.Profiles;

/// <summary>
/// Deterministically samples a <see cref="FaultProfile"/> into a single concrete
/// <see cref="SampledFault"/> using a caller-supplied <see cref="Random"/>. All
/// randomness (entry selection, parameter ranges, latency delay) is drawn from that one
/// RNG, so an identical <c>(seed, profile, draw-sequence)</c> yields an identical fault
/// sequence — the reproducibility guarantee a validation harness needs (D21).
/// </summary>
internal static class FaultProfileSampler
{
    /// <summary>
    /// Draws one fault from <paramref name="profile"/> using <paramref name="rng"/>:
    /// weighted-selects an entry, samples its parameter ranges, and materializes the
    /// matching transform config.
    /// </summary>
    public static SampledFault Sample(FaultProfile profile, Random rng)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rng);

        if (profile.Entries.Count == 0)
        {
            throw new ArgumentException($"Fault profile '{profile.Id}' has no entries to sample.", nameof(profile));
        }

        var index = SelectWeightedIndex(profile.Entries, rng);
        var entry = profile.Entries[index];

        // Sample every declared parameter range up front (deterministic order = JSON order)
        // so the RNG draw sequence is stable regardless of which tokens get substituted.
        var sampledParams = SampleParams(entry, rng);

        return entry.Kind.ToLowerInvariant() switch
        {
            "error" => new SampledFault(
                index,
                SampledFaultKind.Error,
                Error: BuildError(entry, sampledParams),
                Latency: null,
                DropResponse: null,
                sampledParams),

            "latency" => new SampledFault(
                index,
                SampledFaultKind.Latency,
                Error: null,
                Latency: BuildLatency(entry, rng, sampledParams),
                DropResponse: null,
                sampledParams),

            "dropresponse" => new SampledFault(
                index,
                SampledFaultKind.DropResponse,
                Error: null,
                Latency: null,
                DropResponse: new DropResponseConfig(Probability: 1.0, FailFirst: null, MaxFires: null),
                sampledParams),

            _ => throw new InvalidOperationException(
                $"Fault profile '{profile.Id}' entry {index} has unsupported kind '{entry.Kind}'."),
        };
    }

    private static int SelectWeightedIndex(IReadOnlyList<FaultProfileEntry> entries, Random rng)
    {
        var total = 0.0;
        foreach (var entry in entries)
        {
            total += entry.Weight;
        }

        var roll = rng.NextDouble() * total;
        var cumulative = 0.0;
        for (var i = 0; i < entries.Count; i++)
        {
            cumulative += entries[i].Weight;
            if (roll < cumulative)
            {
                return i;
            }
        }

        // Floating-point tail: roll landed exactly at total. Return the last entry.
        return entries.Count - 1;
    }

    private static IReadOnlyDictionary<string, int> SampleParams(FaultProfileEntry entry, Random rng)
    {
        if (entry.ParamRanges is null || entry.ParamRanges.Count == 0)
        {
            return EmptyParams;
        }

        var sampled = new Dictionary<string, int>(entry.ParamRanges.Count, StringComparer.Ordinal);
        foreach (var kv in entry.ParamRanges)
        {
            sampled[kv.Key] = SampleRange(kv.Value, rng);
        }

        return sampled;
    }

    private static ErrorConfig BuildError(FaultProfileEntry entry, IReadOnlyDictionary<string, int> sampledParams)
    {
        if (entry.Status is not int status)
        {
            throw new InvalidOperationException($"Fault profile entry of kind 'error' is missing a status.");
        }

        IReadOnlyDictionary<string, string>? headers = null;
        if (entry.Headers is not null && entry.Headers.Count > 0)
        {
            var resolved = new Dictionary<string, string>(entry.Headers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entry.Headers)
            {
                resolved[kv.Key] = Substitute(kv.Value, sampledParams);
            }

            headers = resolved;
        }

        var body = entry.Body is null ? null : Substitute(entry.Body, sampledParams);
        return new ErrorConfig(status, body, entry.ContentType, headers, Probability: 1.0, FailFirst: null);
    }

    private static LatencyConfig BuildLatency(FaultProfileEntry entry, Random rng, IReadOnlyDictionary<string, int> sampledParams)
    {
        var min = entry.MinMs ?? 0;
        var max = entry.MaxMs ?? min;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        // Draw the actual delay here (same seeded RNG) and pin it as a fixed [delay, delay]
        // window so determinism lives entirely in this sampler rather than the middleware.
        var delay = min == max ? min : rng.Next(min, max + 1);
        return new LatencyConfig(delay, delay, Probability: 1.0, FailFirst: null);
    }

    private static int SampleRange(int[] range, Random rng)
    {
        if (range is null || range.Length == 0)
        {
            return 0;
        }

        if (range.Length == 1)
        {
            return range[0];
        }

        var min = range[0];
        var max = range[1];
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return min == max ? min : rng.Next(min, max + 1);
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, int> sampledParams)
    {
        if (sampledParams.Count == 0 || template.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var result = template;
        foreach (var kv in sampledParams)
        {
            result = result.Replace(
                "${" + kv.Key + "}",
                kv.Value.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyParams =
        new Dictionary<string, int>(0, StringComparer.Ordinal);
}
