// <copyright file="ChaosProxyMesh.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// The handle returned by <see cref="ChaosProxyMeshExtensions.AddChaosProxyMesh"/>. Meshes the
/// service tier eagerly on creation and exposes <see cref="IncludeInfrastructure"/> to opt into
/// the connection-string infra tier. Also surfaces the <see cref="Summary"/> of meshed/skipped
/// edges so the mesh never silently no-ops (R5).
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public sealed class ChaosProxyMesh
{
    private const string LogPrefix = "[Aspire.Hosting.Chaos.Mesh]";

    private readonly MeshBuildContext context;
    private readonly ChaosMeshScope? scope;
    private bool infrastructureIncluded;

    internal ChaosProxyMesh(
        IDistributedApplicationBuilder builder,
        Func<IResource, IResource, bool>? excludeEdge,
        ChaosMeshScope? scope)
    {
        this.Builder = builder;
        this.scope = scope;
        this.context = new MeshBuildContext(builder, excludeEdge, scope);

        new ServiceDiscoveryEdgeProvider().Run(this.context);
        EmitSummary("service tier", this.context.Reports);

        if (scope is not null)
        {
            // Fail-closed backstop: even if a caller forgets to call Seal() explicitly, validate the
            // scope once the full graph is built (BeforeStart fires after construction, so both the
            // service and infra tiers are present). Throwing here aborts host startup before any proxy
            // container runs — the correct outcome for an unmeshable requested edge.
            builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
            {
                this.Seal();
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>Gets the distributed application builder this mesh was attached to.</summary>
    public IDistributedApplicationBuilder Builder { get; }

    /// <summary>Gets the proxies created by this mesh (one per meshed edge).</summary>
    internal IReadOnlyCollection<IResourceBuilder<ChaosProxyResource>> Proxies => this.context.Proxies;

    /// <summary>
    /// Gets the structured per-edge disposition summary: every candidate edge the mesh
    /// considered, meshed or skipped (with reason).
    /// </summary>
    public IReadOnlyList<ChaosMeshEdgeReport> Summary => this.context.Reports;

    /// <summary>
    /// Opts into the infra tier: additionally meshes connection-string edges to managed-infra
    /// resources (Cosmos DB emulator, Azurite Storage queue). Idempotent — calling twice is a
    /// no-op.
    /// </summary>
    /// <returns>The same mesh handle for chaining.</returns>
    public ChaosProxyMesh IncludeInfrastructure()
    {
        if (this.infrastructureIncluded)
        {
            return this;
        }

        this.infrastructureIncluded = true;

        var before = this.context.Reports.Count;
        new ConnectionStringEdgeProvider().Run(this.context);
        EmitSummary("infrastructure tier", this.context.Reports.Skip(before).ToList());
        return this;
    }

    /// <summary>
    /// Runs the fail-closed completeness gate for an active <see cref="ChaosMeshScope"/>: verifies that
    /// every requested edge actually produced a proxy, throwing <see cref="InvalidOperationException"/>
    /// otherwise. A no-op when no scope is active. Idempotent and synchronous — safe to call explicitly
    /// (for fail-fast before any container starts) as well as from the <see cref="BeforeStartEvent"/>
    /// backstop.
    /// </summary>
    /// <returns>The same mesh handle for chaining.</returns>
    public ChaosProxyMesh Seal()
    {
        if (this.scope is null)
        {
            return this;
        }

        this.scope.Validate(this.RealizedProxyNames(), this.context.Reports);
        return this;
    }

    private static void EmitSummary(string phase, IReadOnlyList<ChaosMeshEdgeReport> reports)
    {
        var meshed = reports.Count(r => r.Meshed);
        var skipped = reports.Count - meshed;
        Console.WriteLine($"{LogPrefix} {phase}: {meshed} edge(s) meshed, {skipped} skipped.");
        foreach (var report in reports)
        {
            Console.WriteLine($"{LogPrefix}   {report}");
        }
    }

    private IReadOnlyCollection<string> RealizedProxyNames()
        => this.context.Proxies.Select(p => p.Resource.Name).ToList();
}
