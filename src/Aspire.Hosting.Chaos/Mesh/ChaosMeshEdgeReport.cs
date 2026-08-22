// <copyright file="ChaosMeshEdgeReport.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Hosting.Chaos;

/// <summary>
/// One line of the chaos-mesh startup summary: the disposition of a single candidate edge —
/// either meshed (a proxy was inserted) or skipped (with a reason). Surfaced so the mesh never
/// silently no-ops (R5).
/// </summary>
public sealed class ChaosMeshEdgeReport
{
    internal ChaosMeshEdgeReport(
        string clientName,
        string targetName,
        string provider,
        string tier,
        bool meshed,
        string? proxyName,
        string? skipReason)
    {
        this.ClientName = clientName;
        this.TargetName = targetName;
        this.Provider = provider;
        this.Tier = tier;
        this.Meshed = meshed;
        this.ProxyName = proxyName;
        this.SkipReason = skipReason;
    }

    /// <summary>Gets the client (caller) resource name.</summary>
    public string ClientName { get; }

    /// <summary>Gets the target (callee) resource name.</summary>
    public string TargetName { get; }

    /// <summary>Gets the provider that classified the edge (e.g. <c>ServiceDiscovery</c>, <c>ConnectionString</c>).</summary>
    public string Provider { get; }

    /// <summary>Gets the conceptual tier: <c>service</c> or <c>infra</c>.</summary>
    public string Tier { get; }

    /// <summary>Gets a value indicating whether a proxy was inserted on this edge.</summary>
    public bool Meshed { get; }

    /// <summary>Gets the proxy resource name when <see cref="Meshed"/> is true; otherwise <see langword="null"/>.</summary>
    public string? ProxyName { get; }

    /// <summary>Gets the reason the edge was skipped when <see cref="Meshed"/> is false; otherwise <see langword="null"/>.</summary>
    public string? SkipReason { get; }

    /// <inheritdoc/>
    public override string ToString()
        => this.Meshed
            ? $"MESHED   [{this.Tier}/{this.Provider}] {this.ClientName} -> {this.TargetName} via {this.ProxyName}"
            : $"SKIPPED  [{this.Tier}/{this.Provider}] {this.ClientName} -> {this.TargetName}: {this.SkipReason}";
}
