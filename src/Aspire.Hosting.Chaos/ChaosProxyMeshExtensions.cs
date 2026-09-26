// <copyright file="ChaosProxyMeshExtensions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;

namespace Aspire.Hosting;

/// <summary>
/// Zero-config chaos mesh for <see cref="ChaosProxyResource"/>. Derives its scope from Aspire's
/// resource model: <see cref="AddChaosProxyMesh"/> meshes the <b>service tier</b> (edges where
/// both endpoints are the author's own project/container resources, classified by TYPE not by
/// name), and <see cref="ChaosProxyMesh.IncludeInfrastructure"/> opts into the <b>infra tier</b>
/// (connection-string edges to managed-infra resources like the Cosmos DB emulator and Azurite).
/// </summary>
/// <remarks>
/// Implements the zero-config mesh story from
/// <c>docs/projects/aspire-chaos-proxy/zero-config-mesh.plan.md</c>. Edge-scoped, not N×N: one
/// proxy per existing edge, named <c>mesh-{client}-to-{target}</c>. Idempotent — calling twice is
/// safe (already-meshed edges are skipped). Proxies wait for their target's container to START
/// (not be HEALTHY) and are transparent pass-throughs until a policy is installed, so meshing is
/// never fatal to the client.
/// </remarks>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public static class ChaosProxyMeshExtensions
{
    /// <summary>
    /// Meshes the service tier: inserts a pass-through chaos proxy on every edge where both the
    /// client and target are the author's own services (a <see cref="ProjectResource"/> or an
    /// author-added <see cref="ContainerResource"/>), discovered by resource type. Service
    /// discovery on the client is rewritten so code that resolves <c>http://{target}</c> reaches
    /// the proxy first; <c>WithServiceUrl</c> bindings are routed through their proxy too.
    /// </summary>
    /// <param name="builder">The distributed application builder. Call AFTER all <c>WithReference</c>
    /// / <c>WithServiceUrl</c> wiring is complete — the mesh only sees edges that exist at the time
    /// of this call.</param>
    /// <param name="excludeEdge">Optional <i>exclusion</i> predicate. Receives the client and target
    /// resources; return <see langword="true"/> to EXCLUDE that edge from meshing. Intended for
    /// attribute/type-based exclusions (e.g. exclude dashboard/telemetry resources), NOT a
    /// name allowlist. <see langword="null"/> meshes all eligible edges.</param>
    /// <param name="scope">Optional <i>include</i> allowlist. When non-<see langword="null"/>, ONLY the
    /// edges it contains are meshed and every other eligible edge is left direct (with its authored
    /// <c>WaitFor</c> intact) — the mechanism that scopes the mesh to the edges a run actually needs,
    /// cutting cold-start container tax. <see langword="null"/> (the default) means "full mesh",
    /// byte-identical to the unscoped behaviour. A scope fails closed: call
    /// <see cref="ChaosProxyMesh.Seal"/> (or rely on the automatic pre-start backstop) to throw if any
    /// requested edge could not be meshed.</param>
    /// <returns>A <see cref="ChaosProxyMesh"/> handle. Chain <see cref="ChaosProxyMesh.IncludeInfrastructure"/>
    /// to additionally mesh the infra tier, or read <see cref="ChaosProxyMesh.Summary"/>.</returns>
    /// <example>
    /// <code>
    /// // Zero-config: fault my service graph.
    /// builder.AddChaosProxyMesh();
    ///
    /// // Add datastore chaos (Cosmos, Azurite queue, …).
    /// builder.AddChaosProxyMesh().IncludeInfrastructure();
    ///
    /// // Scope the mesh to a single edge, then fail closed if it can't be meshed.
    /// var scope = ChaosMeshScope.FromEnvironmentValue("armgatewayservice-api-&gt;cosmos");
    /// builder.AddChaosProxyMesh(scope: scope).IncludeInfrastructure().Seal();
    ///
    /// // Optional attribute-based exclusion (NOT a name list).
    /// builder.AddChaosProxyMesh(excludeEdge: (client, target) =>
    ///     target.HasAnnotation&lt;DashboardResourceAnnotation&gt;());
    /// </code>
    /// </example>
    public static ChaosProxyMesh AddChaosProxyMesh(
        this IDistributedApplicationBuilder builder,
        Func<IResource, IResource, bool>? excludeEdge = null,
        ChaosMeshScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new ChaosProxyMesh(builder, excludeEdge, scope);
    }
}
