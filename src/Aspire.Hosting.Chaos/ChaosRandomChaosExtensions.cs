// <copyright file="ChaosRandomChaosExtensions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Chaos.Client;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Tunables for mesh-wide resource-aware random chaos (<see cref="ChaosRandomChaosExtensions.WithRandomChaos(ChaosProxyMesh, double, int?, System.Action{RandomChaosOptions}?)"/>).
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public sealed class RandomChaosOptions
{
    /// <summary>Base per-request fire probability in [0, 1] for every meshed edge. Default 0.1.</summary>
    public double Intensity { get; set; } = 0.1;

    /// <summary>
    /// Global RNG seed. Each proxy derives its own stable sub-seed from this so the whole
    /// mesh is reproducible. Null = each proxy generates and logs its own seed.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>Optional per-proxy global fire cap (blast-radius control). Null = no cap.</summary>
    public int? MaxFires { get; set; }

    /// <summary>
    /// Request path prefixes random chaos must never fault. Defaults to common
    /// health/readiness/startup probes so the AppHost stays bootable.
    /// </summary>
    public IList<string> ExcludePaths { get; set; } = new List<string>
    {
        "/health",
        "/healthz",
        "/ready",
        "/readyz",
        "/alive",
        "/startup",
    };

    /// <summary>
    /// Optional per-fault-profile intensity overrides (e.g. <c>azure.cosmos</c> -&gt; 0.2).
    /// A profile not listed here uses <see cref="Intensity"/>.
    /// </summary>
    public IDictionary<string, double> ProfileIntensity { get; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    internal double IntensityFor(string profileId)
        => this.ProfileIntensity.TryGetValue(profileId, out var value) ? value : this.Intensity;
}

/// <summary>
/// Resource-aware random chaos extensions: fault every meshed edge (or a single proxy)
/// with the failures realistic for its target resource type, sampled (weighted, seeded)
/// from the matching fault profile.
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public static class ChaosRandomChaosExtensions
{
    /// <summary>
    /// Arms resource-aware random chaos on every edge the mesh created: each proxy gets a
    /// random-fault policy whose profile is derived from the proxy's target resource type
    /// (service edges → generic HTTP faults, Cosmos edges → Cosmos faults, etc.). Seeded so
    /// a failing feature-resilience run is reproducible.
    /// </summary>
    /// <param name="mesh">The mesh handle from <c>AddChaosProxyMesh()</c>.</param>
    /// <param name="intensity">Base per-request fire probability in [0, 1]. Default 0.1.</param>
    /// <param name="seed">Global seed; each proxy derives a stable sub-seed. Null = auto + logged.</param>
    /// <param name="configure">Optional further tuning (per-profile intensity, excluded paths, cap).</param>
    /// <returns>The same mesh handle for chaining.</returns>
    public static ChaosProxyMesh WithRandomChaos(
        this ChaosProxyMesh mesh,
        double intensity = 0.1,
        int? seed = null,
        Action<RandomChaosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var options = new RandomChaosOptions { Intensity = intensity, Seed = seed };
        configure?.Invoke(options);

        var armed = 0;
        foreach (var proxy in mesh.Proxies)
        {
            var profileId = ChaosFaultProfiles.ForResource(proxy.Resource);
            var perProxySeed = options.Seed is int s
                ? unchecked(s ^ StableHash(proxy.Resource.Name))
                : (int?)null;

            proxy.WithRandomChaos(
                intensity: options.IntensityFor(profileId),
                seed: perProxySeed,
                profileId: profileId,
                maxFires: options.MaxFires,
                excludePaths: options.ExcludePaths);
            armed++;
        }

        Console.WriteLine(
            $"[Aspire.Hosting.Chaos.Mesh] random chaos armed on {armed} proxy(ies) " +
            $"(intensity {intensity}, seed {(seed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto")}).");

        return mesh;
    }

    /// <summary>
    /// Arms resource-aware random chaos on a single proxy. The fault profile defaults to the
    /// proxy's target resource type (stamped by the mesh) or the generic service profile,
    /// unless <paramref name="profileId"/> overrides it.
    /// </summary>
    /// <param name="proxy">The chaos proxy resource builder.</param>
    /// <param name="intensity">Per-request fire probability in [0, 1]. Default 0.1.</param>
    /// <param name="seed">RNG seed. Null = the server generates and logs one.</param>
    /// <param name="profileId">Fault profile id. Null = derive from the proxy's target kind.</param>
    /// <param name="maxFires">Optional global fire cap. Null = no cap.</param>
    /// <param name="excludePaths">Request path prefixes never faulted.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    public static IResourceBuilder<ChaosProxyResource> WithRandomChaos(
        this IResourceBuilder<ChaosProxyResource> proxy,
        double intensity = 0.1,
        int? seed = null,
        string? profileId = null,
        int? maxFires = null,
        IEnumerable<string>? excludePaths = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        var resolvedProfile = profileId ?? ChaosFaultProfiles.ForResource(proxy.Resource);

        return proxy.WithPolicy(new ChaosPolicy
        {
            Id = $"random-{proxy.Resource.Name}",
            RandomFault = new ChaosRandomFault
            {
                ProfileId = resolvedProfile,
                Intensity = intensity,
                Seed = seed,
                MaxFires = maxFires,
                ExcludePaths = excludePaths?.ToList(),
            },
        });
    }

    // Deterministic (process-independent) string hash so per-proxy sub-seeds are stable
    // across runs — String.GetHashCode is randomized per process and would break replay.
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in value)
            {
                hash = (hash * 31) + c;
            }

            return hash;
        }
    }
}
