// <copyright file="MeshBuildContext.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Shared state threaded through the edge providers during a single mesh build. Owns proxy
/// creation/dedup, the resource-type classification helpers, and the accumulating edge reports.
/// </summary>
internal sealed class MeshBuildContext
{
    internal const string ServiceTier = "service";
    internal const string InfraTier = "infra";

    private const string MeshProxyNamePrefix = "mesh-";
    private const string HttpEndpointName = ChaosProxyResource.HttpEndpointName;

    private readonly Dictionary<string, IResourceBuilder<ChaosProxyResource>> proxies =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IResourceBuilder<ChaosProxyResource>> proxyStartupTails =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> preExistingNames;

    internal MeshBuildContext(
        IDistributedApplicationBuilder builder,
        Func<IResource, IResource, bool>? excludeEdge,
        ChaosMeshScope? scope)
    {
        this.Builder = builder;
        this.ExcludeEdge = excludeEdge;
        this.Scope = scope;
        this.Snapshot = builder.Resources.ToList();
        this.preExistingNames = builder.Resources.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal IDistributedApplicationBuilder Builder { get; }

    internal Func<IResource, IResource, bool>? ExcludeEdge { get; }

    /// <summary>
    /// Gets the optional include-scope. When non-null, only edges it <see cref="ChaosMeshScope.Contains"/>
    /// are meshed; all others are skipped (kept direct) via <see cref="IsOutOfScope"/>.
    /// </summary>
    internal ChaosMeshScope? Scope { get; }

    /// <summary>
    /// Returns whether the edge <c>{client}-&gt;{target}</c> must be skipped because a scope is active and
    /// does not include it. Consulted by every edge provider immediately before the exclusion predicate,
    /// so include-scoping and exclusion share one decision point.
    /// </summary>
    internal bool IsOutOfScope(IResource client, IResource target)
        => this.Scope is not null && !this.Scope.Contains(client.Name, target.Name);

    /// <summary>Gets the resource snapshot taken before any mesh mutation.</summary>
    internal IReadOnlyList<IResource> Snapshot { get; }

    /// <summary>Gets the accumulated per-edge disposition reports.</summary>
    internal List<ChaosMeshEdgeReport> Reports { get; } = new();

    /// <summary>Gets the proxies created during this mesh build (one per meshed edge).</summary>
    internal IReadOnlyCollection<IResourceBuilder<ChaosProxyResource>> Proxies => this.proxies.Values;

    /// <summary>
    /// Builds the canonical proxy name for an edge: <c>mesh-{client}-to-{target}</c>.
    /// </summary>
    internal static string ProxyNameFor(IResource client, IResource target)
        => $"{MeshProxyNamePrefix}{client.Name}-to-{target.Name}";

    /// <summary>
    /// A resource is in the SERVICE tier when it's one of the author's own services — a
    /// <see cref="ProjectResource"/> or an author-added <see cref="ContainerResource"/> — and is
    /// neither a chaos proxy nor a managed-infra/connection-string resource.
    /// </summary>
    internal static bool IsServiceTier(IResource resource)
    {
        if (resource is ChaosProxyResource)
        {
            return false;
        }

        if (resource is IResourceWithConnectionString)
        {
            // Datastore / managed-infra resources are the infra tier, even if they happen to
            // run as a container under the hood (Azurite, Cosmos emulator).
            return false;
        }

        return resource is ProjectResource || resource is ContainerResource;
    }

    /// <summary>
    /// A resource is an INFRA target when it's a managed-infra / connection-string resource
    /// (Cosmos, Storage, Service Bus, Key Vault, …) — never a chaos proxy.
    /// </summary>
    internal static bool IsInfraTarget(IResource resource)
        => resource is not ChaosProxyResource && resource is IResourceWithConnectionString;

    /// <summary>
    /// Resolves the resource that actually hosts the network endpoints for <paramref name="target"/>.
    /// Child resources (e.g. an Azure Storage <c>queues</c> sub-resource) carry the connection
    /// string but their parent owns the container endpoints.
    /// </summary>
    internal static IResourceWithEndpoints? ResolveEndpointHost(IResource target)
    {
        if (target is IResourceWithEndpoints withEndpoints && withEndpoints.GetEndpoints().Any())
        {
            return withEndpoints;
        }

        if (target is IResourceWithParent parented && parented.Parent is IResourceWithEndpoints parentEndpoints)
        {
            return parentEndpoints;
        }

        return null;
    }

    internal static bool HasEndpoint(IResourceWithEndpoints host, string endpointName)
        => host.GetEndpoints().Any(e => string.Equals(e.EndpointName, endpointName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the proxy for <paramref name="proxyName"/>, creating it (and applying
    /// <paramref name="configure"/>) on first use. Idempotency: if the name already existed in
    /// the AppHost before this mesh build started (e.g. a prior <c>AddChaosProxyMesh</c> call or a
    /// manual proxy), returns <see langword="null"/> so the caller skips re-wiring.
    /// </summary>
    internal IResourceBuilder<ChaosProxyResource>? GetOrCreateProxy(
        string proxyName,
        IResource endpointHost,
        Action<IResourceBuilder<ChaosProxyResource>> configure)
    {
        if (this.proxies.TryGetValue(proxyName, out var existing))
        {
            return existing;
        }

        if (this.preExistingNames.Contains(proxyName))
        {
            return null;
        }

        var proxy = this.Builder.AddChaosProxy(proxyName);
        configure(proxy);

        // DCP materializes host-connectivity tunnels while creating dependent containers. Starting
        // several proxies against the same endpoint host concurrently can race that allocation and
        // leave one FailedToStart with "tunnel service ... should have valid address". Serialize only
        // that host-local group; unrelated proxy groups still start in parallel.
        if (this.proxyStartupTails.TryGetValue(endpointHost.Name, out var previousProxy))
        {
            proxy.WaitForStart(previousProxy);
        }

        this.proxyStartupTails[endpointHost.Name] = proxy;
        this.proxies[proxyName] = proxy;
        return proxy;
    }

    internal static EndpointReference ProxyHttpEndpoint(IResourceBuilder<ChaosProxyResource> proxy)
        => proxy.GetEndpoint(HttpEndpointName);

    /// <summary>
    /// Orders the client's start after its own newly-created chaos proxy, so the client's SDK
    /// never dials a proxy whose Kestrel/YARP listener has not started yet.
    /// </summary>
    /// <remarks>
    /// Every proxy is a fresh <c>WithDockerfile</c> container build (see
    /// <see cref="ChaosProxyResourceBuilderExtensions.AddChaosProxy"/>) and only becomes Aspire
    /// "Healthy" once its own <c>/chaos/healthz</c> endpoint starts responding. Before this fix,
    /// nothing gated the client on that timeline — only on whatever <c>WaitFor</c> the AppHost
    /// author already wrote against the ORIGINAL target, which has no relation to the proxy
    /// container's own build/start schedule. For a slow-starting target (e.g. the Cosmos
    /// emulator) that pre-existing wait chain happens to give the comparatively lighter proxy
    /// enough wall-clock time to finish starting first, masking the race; for a fast-starting
    /// target (a project with its own quick health check, or infra gated only by
    /// <c>WaitForStart</c> — container "Running", not fully ready) the client can win the race and
    /// start dialing the proxy before it is listening, producing a connection hang or failure on
    /// first use. This is the same latent defect behind all three previously-excluded mesh edges
    /// (GW Worker -&gt; MIMS; workspace-service/chaos-workspaces-worker -&gt; Azurite queue).
    /// <para>
    /// Safe by construction: the proxy's health check is a pure liveness probe on its own
    /// Kestrel/YARP process, independent of the forwarding target's own readiness, so it can never
    /// form a wait cycle with the client's existing <c>WaitFor(target)</c>.
    /// </para>
    /// <para>
    /// No-ops (does not throw) for a client type that does not support Aspire's wait-for model —
    /// mirrors the existing environment-variable-support skip check already used by both edge
    /// providers, so an edge that would otherwise be meshed is never dropped just because this
    /// ordering could not be added.
    /// </para>
    /// </remarks>
    internal void WaitForOwnProxy(IResource client, IResourceBuilder<ChaosProxyResource> proxy)
    {
        if (client is IResourceWithWaitSupport clientWithWaitSupport)
        {
            this.Builder.CreateResourceBuilder(clientWithWaitSupport).WaitFor(proxy);
        }
    }

    internal void Report(ChaosMeshEdgeReport report) => this.Reports.Add(report);
}
