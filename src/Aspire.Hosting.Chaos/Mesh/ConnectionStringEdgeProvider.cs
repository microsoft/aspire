// <copyright file="ConnectionStringEdgeProvider.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Infra-tier provider (opt-in via <c>IncludeInfrastructure()</c>). Meshes connection-string
/// edges to managed-infra resources, rewriting the client's <c>ConnectionStrings__{name}</c> so
/// its SDK dials the proxy. Ships handlers for the Azure emulators the Uber AppHost uses — the
/// Cosmos DB emulator (HTTPS-terminating) and the Azurite Storage queue endpoint. Unknown infra
/// types are skipped with a visible reason (R5) and remain wireable via the manual
/// <c>AddChaosProxy(...).WithTarget(...)</c> escape hatch.
/// </summary>
internal sealed class ConnectionStringEdgeProvider : IMeshEdgeProvider
{
    private const string ReferenceRelationship = "Reference";
    private const string CosmosEmulatorEndpoint = "emulator";
    private const string QueueEndpoint = "queue";
    private const string HttpEndpointName = ChaosProxyResource.HttpEndpointName;
    private const string HttpsEndpointName = ChaosProxyResource.HttpsEndpointName;

    // Well-known, publicly documented emulator credentials (dev-only). Not secrets.
    // https://learn.microsoft.com/azure/cosmos-db/emulator#authentication
    private const string CosmosEmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    // https://learn.microsoft.com/azure/storage/common/storage-use-azurite#well-known-storage-account-and-key
    private const string AzuriteAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <inheritdoc/>
    public string Name => "ConnectionString";

    /// <inheritdoc/>
    public void Run(MeshBuildContext context)
    {
        foreach (var client in context.Snapshot)
        {
            if (client is ChaosProxyResource)
            {
                continue;
            }

            foreach (var rel in client.Annotations.OfType<ResourceRelationshipAnnotation>().ToList())
            {
                if (!string.Equals(rel.Type, ReferenceRelationship, StringComparison.Ordinal))
                {
                    continue;
                }

                var target = rel.Resource;
                if (target is null || ReferenceEquals(target, client))
                {
                    continue;
                }

                if (!MeshBuildContext.IsInfraTarget(target))
                {
                    continue;
                }

                this.MeshInfraEdge(context, client, target);
            }
        }
    }

    private void MeshInfraEdge(MeshBuildContext context, IResource client, IResource target)
    {
        var proxyName = MeshBuildContext.ProxyNameFor(client, target);

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

        var host = MeshBuildContext.ResolveEndpointHost(target);
        if (host is null)
        {
            context.Report(this.Skip(client, target, proxyName: null, "no resolvable endpoint host for connection-string target"));
            return;
        }

        // Classify by the TARGET resource's own kind, not by whatever endpoints its host exposes.
        // In the real AppHost graph, storage queue/blob/table are sibling CHILD resources of a
        // single Azurite parent, so the parent host exposes queue+blob+table endpoints to ALL of
        // them. Picking a handler off the host's endpoints would mis-route a blob/table reference
        // through the queue handler and rewrite its ConnectionStrings__{name} to a QueueEndpoint.
        switch (ClassifyInfra(target, host))
        {
            case InfraKind.Cosmos:
                if (!MeshBuildContext.HasEndpoint(host, CosmosEmulatorEndpoint))
                {
                    context.Report(this.Skip(client, target, proxyName: null, "Cosmos target exposes no emulator endpoint (real-Azure Cosmos interception not supported in v1)"));
                    return;
                }

                this.MeshCosmos(context, client, target, host, proxyName);
                return;

            case InfraKind.Queue:
                if (!MeshBuildContext.HasEndpoint(host, QueueEndpoint))
                {
                    context.Report(this.Skip(client, target, proxyName: null, "Storage queue target host exposes no queue endpoint"));
                    return;
                }

                this.MeshAzuriteQueue(context, client, target, host, proxyName);
                return;

            case InfraKind.Blob:
                context.Report(this.Skip(client, target, proxyName: null, "Azure Blob storage interception not supported in v1 infra tier (Cosmos emulator + Storage queue only)"));
                return;

            case InfraKind.Table:
                context.Report(this.Skip(client, target, proxyName: null, "Azure Table storage interception not supported in v1 infra tier (Cosmos emulator + Storage queue only)"));
                return;

            case InfraKind.ServiceBus:
                context.Report(this.Skip(client, target, proxyName: null, "Azure Service Bus uses the AMQP protocol, which the HTTP-terminating chaos proxy cannot carry — skipped (no interception capability is lost; its Endpoint=sb://… connection string is left untouched so the consumer does not crash)"));
                return;

            default:
                context.Report(this.Skip(client, target, proxyName: null, "unknown infra type (no Cosmos emulator or Storage queue endpoint)"));
                return;
        }
    }

    /// <summary>
    /// Classifies a connection-string infra target into the handler that knows how to intercept
    /// it. Prefers the target's resource TYPE (so a child <c>AzureQueueStorageResource</c> /
    /// <c>AzureBlobStorageResource</c> / <c>AzureTableStorageResource</c> / <c>AzureCosmosDB*</c>
    /// is classified correctly even when its parent host shares all three storage endpoints).
    /// Falls back to endpoint sniffing only for targets that OWN their endpoints directly (no
    /// parent ambiguity) — the path the lightweight test doubles take.
    /// </summary>
    private static InfraKind ClassifyInfra(IResource target, IResourceWithEndpoints host)
    {
        var typeName = target.GetType().Name;

        // Azure Service Bus (namespace, queue, topic, or subscription) speaks AMQP over TCP, which
        // the HTTP(S)-terminating chaos proxy cannot carry — meshing it gains zero interception
        // capability AND mis-rewrites its connection string (the namespace falls to the "emulator"
        // endpoint fallback below -> Cosmos handler -> "AccountEndpoint=https://..."; a child
        // AzureServiceBusQueueResource would even match the "Queue" check below -> Storage-queue
        // handler), producing a connection string the Service Bus SDK rejects with an
        // ArgumentException that crash-flaps the consumer (e.g. the GW WebJobs worker). Recognize it
        // FIRST and skip it. Must precede the "Queue" check so AzureServiceBusQueueResource is not
        // mistaken for an Azurite Storage queue.
        if (typeName.Contains("ServiceBus", StringComparison.OrdinalIgnoreCase))
        {
            return InfraKind.ServiceBus;
        }

        if (typeName.Contains("Cosmos", StringComparison.OrdinalIgnoreCase))
        {
            return InfraKind.Cosmos;
        }

        if (typeName.Contains("Queue", StringComparison.OrdinalIgnoreCase))
        {
            return InfraKind.Queue;
        }

        if (typeName.Contains("Blob", StringComparison.OrdinalIgnoreCase))
        {
            return InfraKind.Blob;
        }

        if (typeName.Contains("Table", StringComparison.OrdinalIgnoreCase))
        {
            return InfraKind.Table;
        }

        // Endpoint fallback is only safe when the target itself owns the endpoints (host IS the
        // target). A child resource borrowing a shared parent host must NOT be classified this way.
        if (ReferenceEquals(host, target))
        {
            if (MeshBuildContext.HasEndpoint(host, CosmosEmulatorEndpoint))
            {
                return InfraKind.Cosmos;
            }

            if (MeshBuildContext.HasEndpoint(host, QueueEndpoint))
            {
                return InfraKind.Queue;
            }
        }

        return InfraKind.Unknown;
    }

    private void MeshCosmos(MeshBuildContext context, IResource client, IResource target, IResourceWithEndpoints host, string proxyName)
    {
        var hostBuilder = context.Builder.CreateResourceBuilder(host);

        var proxy = context.GetOrCreateProxy(proxyName, host, p =>
        {
            // The Cosmos SDK (Gateway mode) requires https to its target, so terminate TLS on the
            // proxy's https listener and forward to the emulator endpoint. WaitForStart (NOT
            // WaitFor): the proxy only needs the emulator's container Running so its endpoint is
            // allocated — it must NOT block on the emulator's slow /ready health check.
            p.WithTarget(hostBuilder, CosmosEmulatorEndpoint).WaitForStart(hostBuilder);
            p.WithAnnotation(new ChaosTargetKindAnnotation(ChaosTargetKind.Cosmos));
        });

        if (proxy is null)
        {
            context.Report(this.Skip(client, target, proxyName, "already meshed (idempotent skip)"));
            return;
        }

        var connectionName = target.Name;
        ApplyProxyConnectionStringOverride(context, client, connectionName, () =>
        {
            var proxyUrl = proxy.GetEndpoint(HttpsEndpointName).Url.TrimEnd('/');
            // DisableServerCertificateValidation=True lets the SDK accept the proxy's self-signed cert.
            return $"AccountEndpoint={proxyUrl}/;AccountKey={CosmosEmulatorKey};DisableServerCertificateValidation=True";
        });
        context.WaitForOwnProxy(client, proxy);

        context.Report(this.Meshed(client, target, proxyName, "cosmos-emulator"));
    }

    private void MeshAzuriteQueue(MeshBuildContext context, IResource client, IResource target, IResourceWithEndpoints host, string proxyName)
    {
        var hostBuilder = context.Builder.CreateResourceBuilder(host);

        var proxy = context.GetOrCreateProxy(proxyName, host, p =>
        {
            p.WithTarget(hostBuilder, QueueEndpoint).WaitForStart(hostBuilder);
            p.WithAnnotation(new ChaosTargetKindAnnotation(ChaosTargetKind.StorageQueue));
        });

        if (proxy is null)
        {
            context.Report(this.Skip(client, target, proxyName, "already meshed (idempotent skip)"));
            return;
        }

        var connectionName = target.Name;
        ApplyProxyConnectionStringOverride(context, client, connectionName, () =>
        {
            // Present the proxy as an IP-literal emulator endpoint (see ToEmulatorQueueEndpoint): the
            // Azure Storage SDK only applies emulator account-in-path addressing for IP-literal hosts,
            // and a vanity "localhost" makes DurableTask.AzureStorage mis-name its queues after the
            // account and deadlock orchestration dispatch.
            var proxyUrl = ToEmulatorQueueEndpoint(proxy.GetEndpoint(HttpEndpointName).Url);
            return $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={AzuriteAccountKey};QueueEndpoint={proxyUrl}/devstoreaccount1;";
        });
        context.WaitForOwnProxy(client, proxy);

        context.Report(this.Meshed(client, target, proxyName, "azurite-queue"));
    }

    /// <summary>
    /// Applies the client's <c>ConnectionStrings__{name}</c> proxy override so its SDK dials the chaos
    /// proxy — and, critically, so the override SURVIVES a targeted <c>aspire resource rebuild</c>/
    /// <c>restart</c>, not just the initial <c>aspire run</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aspire re-materializes a resource's environment from its annotations on every start: a per-resource
    /// rebuild clears the env-callback caches (<c>DcpExecutor.ForgetCachedCallbackResults</c>) and rebuilds
    /// <c>spec.Env</c> by re-running every <see cref="EnvironmentCallbackAnnotation"/> in annotation order,
    /// last-writer-wins. A build-time <c>WithEnvironment</c> override alone is NOT durable across that
    /// rebuild: the client's own <c>WithReference(&lt;infra&gt;)</c> connection-string annotation re-runs
    /// too and wins the last write, so the rebuilt client dials the infra DIRECTLY and drops the
    /// chaos-proxy interception (observed empirically: the injected fault fires 0 times and the operation
    /// bypasses the proxy after a targeted rebuild). That is exactly why re-meshing used to force a full
    /// <c>aspire stop</c>+<c>run</c> AppHost bounce.
    /// </para>
    /// <para>
    /// Fix: besides the build-time override (which covers the initial run and publish/manifest generation,
    /// where <see cref="BeforeResourceStartedEvent"/> does not fire), on the client's
    /// <see cref="BeforeResourceStartedEvent"/> remove the mesh's prior override and re-append it as a fresh
    /// <see cref="EnvironmentCallbackAnnotation"/> — on EVERY start, not just the first. Aspire publishes that
    /// event (blocking, in subscriber-registration order) immediately before it rebuilds <c>spec.Env</c>, on
    /// BOTH the initial and the restart/rebuild paths. A one-shot append is not enough: its list position
    /// freezes on the first start, so on a later rebuild the client's own per-start <c>WithReference</c>
    /// callback re-resolves the connection string to the direct infra endpoint and, enumerated after the
    /// frozen override, wins. Because the mesh subscribes AFTER the infra/service wiring, its handler runs
    /// last and its fresh append is enumerated LAST by the env gatherer on every start — the final writer of
    /// <c>ConnectionStrings__{name}</c> on cold start and rebuild alike, so the mesh survives an in-place
    /// targeted rebuild and a per-resource fix no longer needs a full AppHost bounce.
    /// </para>
    /// </remarks>
    /// <param name="context">The mesh build context (for the builder + its eventing).</param>
    /// <param name="client">The client resource whose connection string is rewritten to the proxy.</param>
    /// <param name="connectionName">The connection name (<c>ConnectionStrings__{connectionName}</c>).</param>
    /// <param name="buildConnectionString">Builds the proxy connection string, reading the proxy's
    /// endpoint fresh each time it is evaluated (so a proxy restart is picked up too).</param>
    private static void ApplyProxyConnectionStringOverride(
        MeshBuildContext context,
        IResource client,
        string connectionName,
        Func<string> buildConnectionString)
    {
        var envVarName = $"ConnectionStrings__{connectionName}";

        // Build-time override — covers the initial `aspire run` and publish/manifest generation (where
        // resources are not started, so BeforeResourceStartedEvent never fires).
        context.Builder
            .CreateResourceBuilder((IResourceWithEnvironment)client)
            .WithEnvironment(ctx => ctx.EnvironmentVariables[envVarName] = buildConnectionString());

        // Per-start re-application — guarantees the override is the LAST env-callback writer on EVERY start,
        // including a targeted `aspire resource rebuild`/`restart`. This must re-append fresh on every start,
        // NOT append-once: a genuine rebuild re-materializes spec.Env by re-running every
        // EnvironmentCallbackAnnotation in insertion order (last-writer-wins), and the client's own per-start
        // writer that (re)resolves `ConnectionStrings__{name}` to the DIRECT infra endpoint is enumerated
        // AFTER a one-shot override whose list position froze on the first start — so a one-shot override loses
        // the rebuild and the proxy interception drops (the injected fault fires 0 times). The mesh subscribes
        // to BeforeResourceStartedEvent AFTER the infra/service wiring and eventing dispatches subscribers in
        // registration order, so re-appending here runs LAST on every start and makes the proxy override the
        // final writer of `ConnectionStrings__{name}` on cold start AND rebuild. Uses Annotations.Remove/Add
        // (NOT WithAnnotation(..., Replace), whose SingleOrDefault throws once more than one env-callback exists).
        context.Builder.Eventing.Subscribe<BeforeResourceStartedEvent>(client, (evt, cancellationToken) =>
        {
            var annotations = evt.Resource.Annotations;

            // Drop our previously-appended override (marker + its owned env-callback) for this key so we can
            // re-append a fresh pair LAST. Removing precisely the tracked callback instance leaves the client's
            // own WithReference callback untouched.
            foreach (var stale in annotations
                         .OfType<ChaosMeshConnectionStringOverrideAnnotation>()
                         .Where(a => string.Equals(a.EnvironmentVariable, envVarName, StringComparison.Ordinal))
                         .ToList())
            {
                annotations.Remove(stale.OverrideCallback);
                annotations.Remove(stale);
            }

            var overrideCallback = new EnvironmentCallbackAnnotation(
                ctx => ctx.EnvironmentVariables[envVarName] = buildConnectionString());
            annotations.Add(new ChaosMeshConnectionStringOverrideAnnotation(envVarName, overrideCallback));
            annotations.Add(overrideCallback);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Normalizes a chaos-proxy endpoint URL into a faithful Azure Storage emulator queue endpoint.
    /// The Azure Storage SDK only applies emulator IP-style (account-in-path) addressing when the
    /// host is an IP literal; with the vanity loopback host <c>localhost</c> the SDK takes a
    /// degenerate path and DurableTask.AzureStorage names its control + work-item queues after the
    /// account (e.g. <c>devstoreaccount1</c>) instead of <c>{taskhub}-control-NN</c>, collapsing
    /// every partition onto one collided queue and deadlocking orchestration dispatch. The loopback
    /// host is therefore rewritten to its IP literal. In publish mode the URL is a manifest
    /// expression (not an absolute URL), so it is returned untouched.
    /// </summary>
    internal static string ToEmulatorQueueEndpoint(string proxyUrl)
    {
        if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://127.0.0.1:{uri.Port}";
        }

        return proxyUrl.TrimEnd('/');
    }

    private ChaosMeshEdgeReport Meshed(IResource client, IResource target, string proxyName, string handler)
        => new(client.Name, target.Name, $"{this.Name}/{handler}", MeshBuildContext.InfraTier, meshed: true, proxyName, skipReason: null);

    private ChaosMeshEdgeReport Skip(IResource client, IResource target, string? proxyName, string reason)
        => new(client.Name, target.Name, this.Name, MeshBuildContext.InfraTier, meshed: false, proxyName, reason);

    /// <summary>The interception handler an infra target maps to.</summary>
    private enum InfraKind
    {
        /// <summary>Unrecognized infra type — skipped (R5), still wireable via the manual escape hatch.</summary>
        Unknown,

        /// <summary>Cosmos DB emulator (HTTPS-terminating proxy + connection-string rewrite).</summary>
        Cosmos,

        /// <summary>Azurite Storage queue endpoint.</summary>
        Queue,

        /// <summary>Azure Blob storage — not intercepted in v1.</summary>
        Blob,

        /// <summary>Azure Table storage — not intercepted in v1.</summary>
        Table,

        /// <summary>Azure Service Bus (namespace/queue/topic) — AMQP, not carriable by the HTTP proxy; skipped.</summary>
        ServiceBus,
    }
}
