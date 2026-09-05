// <copyright file="ServiceDiscoveryEdgeProvider.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Service-tier provider. Meshes edges where BOTH endpoints are the author's own services
/// (project / author-added container), redirecting the client either via the standard
/// <c>services__{target}__http__0</c> service-discovery override (for <c>WithReference</c> edges)
/// or via a custom env var (for <see cref="ServiceUrlBindingAnnotation"/> edges from
/// <c>WithServiceUrl</c>).
/// </summary>
internal sealed class ServiceDiscoveryEdgeProvider : IMeshEdgeProvider
{
    private const string ReferenceRelationship = "Reference";
    private const string HttpEndpointName = ChaosProxyResource.HttpEndpointName;

    /// <inheritdoc/>
    public string Name => "ServiceDiscovery";

    /// <inheritdoc/>
    public void Run(MeshBuildContext context)
    {
        foreach (var client in context.Snapshot)
        {
            if (client is ChaosProxyResource)
            {
                continue;
            }

            // 1) WithReference edges (Aspire service discovery). Materialize before iterating —
            // meshing mutates the client's annotation collection (WithEnvironment).
            foreach (var rel in client.Annotations.OfType<ResourceRelationshipAnnotation>().ToList())
            {
                if (!string.Equals(rel.Type, ReferenceRelationship, StringComparison.Ordinal))
                {
                    continue;
                }

                var target = rel.Resource;
                if (target is null || ReferenceEquals(target, client) || target is ChaosProxyResource)
                {
                    continue;
                }

                // Infra targets belong to the connection-string provider, not here.
                if (!MeshBuildContext.IsServiceTier(target))
                {
                    continue;
                }

                if (!MeshBuildContext.IsServiceTier(client))
                {
                    continue;
                }

                this.MeshServiceDiscoveryEdge(context, client, target);
            }

            // 2) WithServiceUrl edges (custom env-var bindings). Like WithReference edges, these
            // are SERVICE-tier only: both ends must be the author's own project/container. A
            // custom env var pointing at infra (or a non-service client) is NOT a service edge.
            foreach (var binding in client.Annotations.OfType<ServiceUrlBindingAnnotation>().ToList())
            {
                var target = binding.Target;
                if (ReferenceEquals(target, client) || target is ChaosProxyResource)
                {
                    continue;
                }

                if (!MeshBuildContext.IsServiceTier(client))
                {
                    context.Report(this.Skip(client, target, proxyName: null, $"client is not a service-tier resource (binding {binding.EnvironmentVariable})"));
                    continue;
                }

                if (!MeshBuildContext.IsServiceTier(target))
                {
                    context.Report(this.Skip(client, target, proxyName: null, $"target is not a service-tier resource (binding {binding.EnvironmentVariable}); use IncludeInfrastructure() for infra edges"));
                    continue;
                }

                this.MeshServiceUrlEdge(context, client, target, binding.EnvironmentVariable);
            }
        }
    }

    private void MeshServiceDiscoveryEdge(MeshBuildContext context, IResource client, IResource target)
    {
        var proxyName = MeshBuildContext.ProxyNameFor(client, target);

        if (target is not IResourceWithEndpoints targetEndpoints ||
            !MeshBuildContext.HasEndpoint(targetEndpoints, HttpEndpointName))
        {
            context.Report(this.Skip(client, target, proxyName: null, "target exposes no http endpoint"));
            return;
        }

        if (client is not IResourceWithEnvironment)
        {
            context.Report(this.Skip(client, target, proxyName: null, "client has no environment-variable support"));
            return;
        }

        if (context.IsOutOfScope(client, target))
        {
            context.Report(this.Skip(client, target, proxyName: null, "out of mesh scope"));
            return;
        }

        if (context.ExcludeEdge is not null && context.ExcludeEdge(client, target))
        {
            context.Report(this.Skip(client, target, proxyName: null, "excluded by predicate"));
            return;
        }

        var targetBuilder = context.Builder.CreateResourceBuilder(targetEndpoints);
        var proxy = context.GetOrCreateProxy(proxyName, target, p => p
            .WithTarget(targetBuilder)
            .WithAnnotation(new ChaosTargetKindAnnotation(ChaosTargetKind.Service)));
        if (proxy is null)
        {
            context.Report(this.Skip(client, target, proxyName, "already meshed (idempotent skip)"));
            return;
        }

        var clientBuilder = context.Builder.CreateResourceBuilder((IResourceWithEnvironment)client);
        clientBuilder.WithEnvironment(
            $"services__{target.Name}__{HttpEndpointName}__0",
            MeshBuildContext.ProxyHttpEndpoint(proxy));
        context.WaitForOwnProxy(client, proxy);

        context.Report(this.Meshed(client, target, proxyName));
    }

    private void MeshServiceUrlEdge(MeshBuildContext context, IResource client, IResource target, string envVar)
    {
        var proxyName = MeshBuildContext.ProxyNameFor(client, target);

        if (target is not IResourceWithEndpoints targetEndpoints ||
            !MeshBuildContext.HasEndpoint(targetEndpoints, HttpEndpointName))
        {
            context.Report(this.Skip(client, target, proxyName: null, $"target exposes no http endpoint (binding {envVar})"));
            return;
        }

        if (client is not IResourceWithEnvironment)
        {
            context.Report(this.Skip(client, target, proxyName: null, "client has no environment-variable support"));
            return;
        }

        if (context.IsOutOfScope(client, target))
        {
            context.Report(this.Skip(client, target, proxyName: null, "out of mesh scope"));
            return;
        }

        if (context.ExcludeEdge is not null && context.ExcludeEdge(client, target))
        {
            context.Report(this.Skip(client, target, proxyName: null, "excluded by predicate"));
            return;
        }

        // Dedup: a WithReference edge to the same target may have already created this proxy in
        // the same build. Reuse it and just add the custom env-var override.
        var targetBuilder = context.Builder.CreateResourceBuilder(targetEndpoints);
        var proxy = context.GetOrCreateProxy(proxyName, target, p => p
            .WithTarget(targetBuilder)
            .WithAnnotation(new ChaosTargetKindAnnotation(ChaosTargetKind.Service)));
        if (proxy is null)
        {
            context.Report(this.Skip(client, target, proxyName, "already meshed (idempotent skip)"));
            return;
        }

        var clientBuilder = context.Builder.CreateResourceBuilder((IResourceWithEnvironment)client);
        clientBuilder.WithEnvironment(envVar, MeshBuildContext.ProxyHttpEndpoint(proxy));
        context.WaitForOwnProxy(client, proxy);

        context.Report(this.Meshed(client, target, proxyName));
    }

    private ChaosMeshEdgeReport Meshed(IResource client, IResource target, string proxyName)
        => new(client.Name, target.Name, this.Name, MeshBuildContext.ServiceTier, meshed: true, proxyName, skipReason: null);

    private ChaosMeshEdgeReport Skip(IResource client, IResource target, string? proxyName, string reason)
        => new(client.Name, target.Name, this.Name, MeshBuildContext.ServiceTier, meshed: false, proxyName, reason);
}
