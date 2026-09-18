// <copyright file="ChaosProxyMeshZeroConfigTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests the zero-config mesh: type-derived service-tier classification, the opt-in infra tier
/// (Cosmos + Azurite queue), <c>WithServiceUrl</c> bindings, idempotency, and the observability
/// summary. See <c>docs/projects/aspire-chaos-proxy/zero-config-mesh.plan.md</c>.
/// </summary>
[SuppressMessage("AspireExperimental", "ASPIRECHAOS001", Justification = "test")]
public class ChaosProxyMeshZeroConfigTests
{
    private static IDistributedApplicationBuilder CreateBuilder()
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            AssemblyName = typeof(ChaosProxyMeshZeroConfigTests).Assembly.GetName().Name,
        });
    }

    private static IResourceBuilder<ContainerResource> AddService(IDistributedApplicationBuilder builder, string name)
        => builder.AddContainer(name, "fake-image").WithHttpEndpoint(targetPort: 8080, name: "http");

    // Real ProjectResource (from AddProject<T>) carries no http endpoint until configured and
    // would try to read launch settings off disk; construct the resource directly and give it an
    // http endpoint so the mesh's type-based service-tier classification is exercised against the
    // genuine ProjectResource type rather than a container stand-in.
    private static IResourceBuilder<ProjectResource> AddProjectService(IDistributedApplicationBuilder builder, string name)
        => builder.AddResource(new ProjectResource(name)).WithHttpEndpoint(targetPort: 8080, name: "http");

    private static IResourceBuilder<FakeConnectionStringResource> AddInfra(
        IDistributedApplicationBuilder builder,
        string name,
        string endpointName)
    {
        var resource = new FakeConnectionStringResource(name);
        return builder.AddResource(resource)
            .WithEndpoint(targetPort: 9000, name: endpointName, scheme: "https");
    }

    // ---- service-tier classification -----------------------------------------------------

    [Fact]
    public void ServiceTier_ProjectToProject_IsMeshed()
    {
        var builder = CreateBuilder();
        var target = AddProjectService(builder, "target");
        AddProjectService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-target");
    }

    [Fact]
    public void ServiceTier_ProjectToContainer_IsMeshed()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "sidecar");
        AddProjectService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-sidecar");
    }

    [Fact]
    public void ServiceTier_ProjectToCosmos_NotMeshed_WithoutIncludeInfrastructure()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        AddService(builder, "client").WithReference(cosmos);

        builder.AddChaosProxyMesh();

        Assert.DoesNotContain(builder.Resources, r => r.Name.StartsWith("mesh-", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceTier_ProjectToStorage_NotMeshed_WithoutIncludeInfrastructure()
    {
        var builder = CreateBuilder();
        var queues = AddInfra(builder, "queues", "queue");
        AddService(builder, "client").WithReference(queues);

        builder.AddChaosProxyMesh();

        Assert.DoesNotContain(builder.Resources, r => r.Name.StartsWith("mesh-", StringComparison.Ordinal));
    }

    // ---- infra tier ----------------------------------------------------------------------

    [Fact]
    public void IncludeInfrastructure_MeshesCosmosEdge()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        AddService(builder, "client").WithReference(cosmos);

        var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();

        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-cosmos");
        Assert.Contains(
            mesh.Summary,
            r => r.Meshed && r.TargetName == "cosmos" && r.Provider.Contains("cosmos-emulator", StringComparison.Ordinal));
    }

    [Fact]
    public void IncludeInfrastructure_MeshesAzuriteQueueEdge()
    {
        var builder = CreateBuilder();
        var queues = AddInfra(builder, "queues", "queue");
        AddService(builder, "client").WithReference(queues);

        var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();

        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-queues");
        Assert.Contains(
            mesh.Summary,
            r => r.Meshed && r.TargetName == "queues" && r.Provider.Contains("azurite-queue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncludeInfrastructure_CosmosOverride_ReAppliedOnResourceStart_ForRebuildSurvival()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        var client = AddService(builder, "client").WithReference(cosmos);

        builder.AddChaosProxyMesh().IncludeInfrastructure();

        // Meshing installs the build-time override (an EnvironmentCallbackAnnotation) but NOT yet the per-start
        // marker — that is appended lazily on the first BeforeResourceStartedEvent.
        var envCallbacksAfterMesh = client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();
        Assert.Empty(client.Resource.Annotations.OfType<ChaosMeshConnectionStringOverrideAnnotation>());

        // Rebuild survival: on a targeted `aspire resource rebuild`, Aspire re-materializes env from the
        // resource's annotations (in order, last-writer-wins) and fires BeforeResourceStartedEvent immediately
        // before. Without per-start re-application the client's own WithReference value would win on the rebuild
        // and drop the proxy. The mesh's handler must re-append the proxy override as a FRESH, LAST env-callback
        // on EVERY event (not once) so it wins the rebuild's env rebuild. Assert publishing the event appends
        // exactly one marker + one env-callback (enumerated last → wins). The override's proxy VALUE is covered
        // by live validation.
        using var app = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(client.Resource, app.Services),
            CancellationToken.None);

        var marker = Assert.Single(client.Resource.Annotations.OfType<ChaosMeshConnectionStringOverrideAnnotation>());
        Assert.Equal("ConnectionStrings__cosmos", marker.EnvironmentVariable);
        Assert.Equal(
            envCallbacksAfterMesh + 1,
            client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());

        // The per-start override must be the LAST env-callback so it beats the client's own WithReference
        // connection-string annotation on the rebuild's in-order, last-writer-wins env rebuild. The marker
        // tracks that exact owned instance.
        var firstOverride = client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Last();
        Assert.Same(marker.OverrideCallback, firstOverride);

        // Idempotent BY REPLACEMENT across restarts: a second start (a rebuild) removes the prior override and
        // re-appends a FRESH one last — no duplicate accumulation, and the fresh callback (a different instance)
        // is once again the last env-callback so it out-orders any writer that re-runs on the rebuild.
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(client.Resource, app.Services),
            CancellationToken.None);

        var markerAfterRebuild = Assert.Single(client.Resource.Annotations.OfType<ChaosMeshConnectionStringOverrideAnnotation>());
        Assert.Equal(
            envCallbacksAfterMesh + 1,
            client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
        var secondOverride = client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Last();
        Assert.NotSame(firstOverride, secondOverride);
        Assert.Same(markerAfterRebuild.OverrideCallback, secondOverride);
    }

    [Fact]
    public void IncludeInfrastructure_UnknownInfraType_SkippedWithReason()
    {
        var builder = CreateBuilder();
        var blobs = AddInfra(builder, "blobs", "blob");
        AddService(builder, "client").WithReference(blobs);

        var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-client-to-blobs");
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.TargetName == "blobs" && r.SkipReason!.Contains("unknown infra type", StringComparison.Ordinal));
    }

    // ---- WithServiceUrl ------------------------------------------------------------------

    [Fact]
    public void WithServiceUrl_RecordsBindingAnnotation()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "workspaces");
        var client = AddService(builder, "gateway").WithServiceUrl("WORKSPACES__SERVICEBASEURL", target);

        var binding = client.Resource.Annotations.OfType<ServiceUrlBindingAnnotation>().Single();
        Assert.Equal("WORKSPACES__SERVICEBASEURL", binding.EnvironmentVariable);
        Assert.Same(target.Resource, binding.Target);
    }

    [Fact]
    public async Task WithServiceUrl_Edge_IsMeshedAndOverridesEnvVar()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "workspaces");

        // No WithReference here — the ONLY thing exposing the edge is the WithServiceUrl binding.
        var client = AddService(builder, "gateway").WithServiceUrl("WORKSPACES__SERVICEBASEURL", target);

        var mesh = builder.AddChaosProxyMesh();

        Assert.Contains(builder.Resources, r => r.Name == "mesh-gateway-to-workspaces");
        Assert.Contains(
            mesh.Summary,
            r => r.Meshed && r.ClientName == "gateway" && r.TargetName == "workspaces");

        // The env var must now resolve to the mesh proxy, not the raw workspaces endpoint.
        // The raw target expression ({workspaces.bindings.http.url}) does not contain the proxy
        // name, so asserting the proxy name is present proves the override took effect.
#pragma warning disable CS0618 // GetEnvironmentVariableValuesAsync is the supported net8 evaluation path; ExecutionConfigurationBuilder is net10-only.
        var env = await client.Resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
#pragma warning restore CS0618
        Assert.True(env.TryGetValue("WORKSPACES__SERVICEBASEURL", out var url));
        Assert.Contains("mesh-gateway-to-workspaces", url, StringComparison.Ordinal);
    }

    [Fact]
    public void WithServiceUrl_ToInfraTarget_SkippedNotMeshed()
    {
        var builder = CreateBuilder();

        // A custom-env binding pointing at a connection-string infra resource is NOT a service
        // edge: both ends must be service-tier. It must be skipped (with a service-tier reason),
        // never meshed by the service-discovery provider.
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        var client = AddService(builder, "gateway").WithServiceUrl("DB__URL", cosmos);

        var mesh = builder.AddChaosProxyMesh();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-gateway-to-cosmos");
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.ClientName == "gateway" && r.TargetName == "cosmos"
                && r.SkipReason!.Contains("service-tier", StringComparison.Ordinal));
    }

    // ---- child storage resources (real-graph shape) --------------------------------------

    [Fact]
    public void IncludeInfrastructure_QueueChild_Meshed_BlobAndTableChildren_NotRewrittenToQueue()
    {
        var builder = CreateBuilder();

        // A single Azurite parent host exposes queue+blob+table endpoints; queue/blob/table are
        // sibling CHILD connection-string resources that all resolve to that shared host. The
        // mesh must classify by the child's TYPE, not the host's endpoints, so blob/table are NOT
        // rewritten to the queue endpoint.
        var host = builder.AddResource(new FakeStorageHost("storage"))
            .WithEndpoint(targetPort: 10001, name: "queue", scheme: "http")
            .WithEndpoint(targetPort: 10000, name: "blob", scheme: "http")
            .WithEndpoint(targetPort: 10002, name: "table", scheme: "http");

        var queues = builder.AddResource(new FakeAzureQueueStorageResource("queues", host.Resource));
        var blobs = builder.AddResource(new FakeAzureBlobStorageResource("blobs", host.Resource));
        var tables = builder.AddResource(new FakeAzureTableStorageResource("tables", host.Resource));

        AddService(builder, "client")
            .WithReference(queues)
            .WithReference(blobs)
            .WithReference(tables);

        var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();

        // Queue child IS meshed via the azurite-queue handler.
        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-queues");
        Assert.Contains(
            mesh.Summary,
            r => r.Meshed && r.TargetName == "queues" && r.Provider.Contains("azurite-queue", StringComparison.Ordinal));

        // Blob + Table children are NOT meshed (no proxy, no connection-string rewrite to a queue).
        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-client-to-blobs");
        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-client-to-tables");
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.TargetName == "blobs" && r.SkipReason!.Contains("Blob", StringComparison.Ordinal));
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.TargetName == "tables" && r.SkipReason!.Contains("Table", StringComparison.Ordinal));
    }

    [Fact]
    public void IncludeInfrastructure_ServiceBus_SkippedNotMeshed_AmqpNotCarriableByHttpProxy()
    {
        var builder = CreateBuilder();

        // The Azure Service Bus emulator is a standalone connection-string resource that owns an
        // "emulator" endpoint — exactly the shape that, before the ServiceBus skip, fell through
        // ClassifyInfra's type checks to the HasEndpoint(host, "emulator") Cosmos fallback and got
        // an "AccountEndpoint=https://…" (Cosmos-format) rewrite that the Service Bus SDK rejects
        // with an ArgumentException, crash-flapping the consumer (the GW WebJobs worker). A child
        // Service Bus QUEUE additionally carries "Queue" in its type name and would have matched the
        // Azurite Storage-queue handler. Both must be skipped: AMQP is not carriable by the HTTP
        // chaos proxy, so meshing it gains nothing AND corrupts the connection string.
        var serviceBus = builder.AddResource(new FakeAzureServiceBusResource("servicebus"))
            .WithEndpoint(targetPort: 5672, name: "emulator", scheme: "https");
        var sbQueue = builder.AddResource(new FakeAzureServiceBusQueueResource("sbqueue", serviceBus.Resource));

        AddService(builder, "client")
            .WithReference(serviceBus)
            .WithReference(sbQueue);

        var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();

        // No proxy is inserted for either Service Bus edge.
        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-client-to-servicebus");
        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-client-to-sbqueue");

        // Both are skipped with the Service-Bus/AMQP reason — NOT mis-meshed as cosmos-emulator or
        // azurite-queue (the two regressions the skip prevents).
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.TargetName == "servicebus" && r.SkipReason!.Contains("Service Bus", StringComparison.Ordinal));
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.TargetName == "sbqueue" && r.SkipReason!.Contains("Service Bus", StringComparison.Ordinal));
        Assert.DoesNotContain(
            mesh.Summary,
            r => r.Meshed && (r.TargetName == "servicebus" || r.TargetName == "sbqueue"));
    }

    // ---- idempotency ---------------------------------------------------------------------

    [Fact]
    public void Idempotency_SecondMeshCall_AddsNoDuplicateProxies()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        var target = AddService(builder, "target");
        var client = AddService(builder, "client").WithReference(target.GetEndpoint("http"));
        client.WithReference(cosmos);

        builder.AddChaosProxyMesh().IncludeInfrastructure();
        builder.AddChaosProxyMesh().IncludeInfrastructure();

        var proxies = builder.Resources.Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal)).ToList();
        Assert.Equal(proxies.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), proxies.Count);
        Assert.Contains(proxies, p => p.Name == "mesh-client-to-target");
        Assert.Contains(proxies, p => p.Name == "mesh-client-to-cosmos");
    }

    // ---- per-proxy image build (correctness) ---------------------------------------------

    [Fact]
    public void PerProxyImage_MeshWithManyEdges_EachProxyBuildsItsOwnImage()
    {
        var builder = CreateBuilder();

        // A fan-out mesh: one client referencing many targets → many mesh-* proxy edges.
        var client = AddService(builder, "client");
        for (var i = 0; i < 8; i++)
        {
            var target = AddService(builder, $"target{i}");
            client.WithReference(target.GetEndpoint("http"));
        }

        builder.AddChaosProxyMesh();

        var proxies = builder.Resources
            .OfType<ChaosProxyResource>()
            .Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal))
            .ToList();

        // Sanity: the mesh really did create many edges...
        Assert.True(proxies.Count >= 8, $"expected >= 8 mesh proxies, got {proxies.Count}");

        // CORRECTNESS: every edge builds its OWN image (one WithDockerfile per proxy). A "build once
        // + shared WithImage tag" scheme fails non-owner proxies with 'image not found' on a clean
        // image cache (Aspire's WithDockerfile ignores the WithImage tag for the build output), so
        // per-proxy WithDockerfile is the correct shape; the container layer cache keeps builds 2..N
        // cheap since every edge's Dockerfile layers are byte-identical.
        Assert.Equal(
            proxies.Count,
            proxies.Count(p => p.Annotations.OfType<DockerfileBuildAnnotation>().Any()));

        // And every proxy resolves to its OWN distinct per-edge image name.
        foreach (var proxy in proxies)
        {
            var image = proxy.Annotations.OfType<ContainerImageAnnotation>().Single();
            Assert.Equal($"aspire-hosting-chaosproxy-{proxy.Name.ToLowerInvariant()}", image.Image);
            Assert.Equal("local", image.Tag);
        }

        var distinctImages = proxies
            .Select(p => p.Annotations.OfType<ContainerImageAnnotation>().Single().Image)
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.Equal(proxies.Count, distinctImages);
    }

    // ---- observability -------------------------------------------------------------------

    [Fact]
    public void Observability_SummaryEmitted_WithMeshedAndSkippedEdges()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "target");
        var blobs = AddInfra(builder, "blobs", "blob");
        var client = AddService(builder, "client").WithReference(target.GetEndpoint("http"));
        client.WithReference(blobs);

        var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();

        Assert.NotEmpty(mesh.Summary);
        Assert.Contains(mesh.Summary, r => r.Meshed);
        Assert.Contains(mesh.Summary, r => !r.Meshed && r.SkipReason is not null);
    }

    // ---- client waits for its own proxy (startup-ordering fix) ---------------------------
    //
    // Regression coverage for the race that forced armGatewayWorker->mimsApi and the two
    // client->DtfxQueues edges to be temporarily excluded from the mesh: meshing rewrote the
    // client's connection info to point at a freshly created chaos-proxy container but never
    // ordered the client's start after that proxy's own start/health check, so a fast-starting
    // client could dial the proxy before its Kestrel/YARP listener (and Dockerfile build) had
    // finished. These tests assert every edge kind (service-discovery, WithServiceUrl, and both
    // infra-tier providers) now emits a WaitAnnotation from the client onto its own proxy.

    [Fact]
    public void ServiceDiscoveryEdge_ClientWaitsForOwnProxy()
    {
        var builder = CreateBuilder();
        var target = AddProjectService(builder, "target");
        var client = AddProjectService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        Assert.Contains(
            client.Resource.Annotations.OfType<WaitAnnotation>(),
            w => w.Resource.Name == "mesh-client-to-target");
    }

    [Fact]
    public void WithServiceUrl_Edge_ClientWaitsForOwnProxy()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "workspaces");
        var client = AddService(builder, "gateway").WithServiceUrl("WORKSPACES__SERVICEBASEURL", target);

        builder.AddChaosProxyMesh();

        Assert.Contains(
            client.Resource.Annotations.OfType<WaitAnnotation>(),
            w => w.Resource.Name == "mesh-gateway-to-workspaces");
    }

    [Fact]
    public void IncludeInfrastructure_CosmosEdge_ClientWaitsForOwnProxy()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        var client = AddService(builder, "client").WithReference(cosmos);

        builder.AddChaosProxyMesh().IncludeInfrastructure();

        Assert.Contains(
            client.Resource.Annotations.OfType<WaitAnnotation>(),
            w => w.Resource.Name == "mesh-client-to-cosmos");
    }

    [Fact]
    public void IncludeInfrastructure_AzuriteQueueEdge_ClientWaitsForOwnProxy()
    {
        // Same shape as the reported failure: a queue-tier client (e.g. workspace-service,
        // chaos-workspaces-worker) referencing an Azurite queue child resource.
        var builder = CreateBuilder();
        var queues = AddInfra(builder, "queues", "queue");
        var client = AddService(builder, "client").WithReference(queues);

        builder.AddChaosProxyMesh().IncludeInfrastructure();

        Assert.Contains(
            client.Resource.Annotations.OfType<WaitAnnotation>(),
            w => w.Resource.Name == "mesh-client-to-queues");
    }

    [Fact]
    public void SharedServiceTarget_ProxyStartsAreSerializedWithinTargetGroup()
    {
        var builder = CreateBuilder();
        var target = AddProjectService(builder, "target");
        AddProjectService(builder, "client-one").WithReference(target.GetEndpoint("http"));
        AddProjectService(builder, "client-two").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        var first = builder.Resources.OfType<ChaosProxyResource>().Single(r => r.Name == "mesh-client-one-to-target");
        var second = builder.Resources.OfType<ChaosProxyResource>().Single(r => r.Name == "mesh-client-two-to-target");
        Assert.DoesNotContain(first.Annotations.OfType<WaitAnnotation>(), w => w.Resource is ChaosProxyResource);
        Assert.Contains(second.Annotations.OfType<WaitAnnotation>(), w => w.Resource.Name == first.Name);
    }

    [Fact]
    public void DistinctServiceTargets_ProxyStartsRemainParallel()
    {
        var builder = CreateBuilder();
        var targetOne = AddProjectService(builder, "target-one");
        var targetTwo = AddProjectService(builder, "target-two");
        AddProjectService(builder, "client-one").WithReference(targetOne.GetEndpoint("http"));
        AddProjectService(builder, "client-two").WithReference(targetTwo.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        var proxies = builder.Resources.OfType<ChaosProxyResource>().ToList();
        Assert.All(
            proxies,
            proxy => Assert.DoesNotContain(
                proxy.Annotations.OfType<WaitAnnotation>(),
                wait => wait.Resource is ChaosProxyResource));
    }

    [Fact]
    public void SharedInfraHost_ProxyStartsAreSerializedAcrossQueueChildren()
    {
        var builder = CreateBuilder();
        var host = builder.AddResource(new FakeStorageHost("storage"))
            .WithEndpoint(targetPort: 10001, name: "queue", scheme: "http");
        var queueOne = builder.AddResource(new FakeAzureQueueStorageResource("queue-one", host.Resource));
        var queueTwo = builder.AddResource(new FakeAzureQueueStorageResource("queue-two", host.Resource));
        AddService(builder, "client-one").WithReference(queueOne);
        AddService(builder, "client-two").WithReference(queueTwo);

        builder.AddChaosProxyMesh().IncludeInfrastructure();

        var first = builder.Resources.OfType<ChaosProxyResource>().Single(r => r.Name == "mesh-client-one-to-queue-one");
        var second = builder.Resources.OfType<ChaosProxyResource>().Single(r => r.Name == "mesh-client-two-to-queue-two");
        Assert.Contains(second.Annotations.OfType<WaitAnnotation>(), w => w.Resource.Name == first.Name);
    }

    /// <summary>
    /// Minimal connection-string resource for exercising the infra tier without taking a
    /// dependency on the Azure hosting packages. Implements the only abstract member of
    /// <see cref="IResourceWithConnectionString"/>; the rest are default interface methods.
    /// </summary>
    private sealed class FakeConnectionStringResource : Resource, IResourceWithConnectionString, IResourceWithEndpoints
    {
        public FakeConnectionStringResource(string name)
            : base(name)
        {
        }

        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"UseDevelopmentStorage=true");
    }

    /// <summary>An endpoint-owning parent host (the Azurite container) shared by storage children.</summary>
    private sealed class FakeStorageHost : Resource, IResourceWithEndpoints
    {
        public FakeStorageHost(string name)
            : base(name)
        {
        }
    }

    /// <summary>
    /// Base for a storage CHILD connection-string resource: it owns no endpoints itself and
    /// resolves to its parent host. Type-name carries the storage kind (Queue/Blob/Table) the
    /// mesh classifies on, mirroring the real <c>Azure*StorageResource</c> types.
    /// </summary>
    private abstract class FakeStorageChildResource : Resource, IResourceWithConnectionString, IResourceWithParent
    {
        protected FakeStorageChildResource(string name, IResource parent)
            : base(name)
        {
            this.Parent = parent;
        }

        public IResource Parent { get; }

        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"UseDevelopmentStorage=true");
    }

    private sealed class FakeAzureQueueStorageResource : FakeStorageChildResource
    {
        public FakeAzureQueueStorageResource(string name, IResource parent)
            : base(name, parent)
        {
        }
    }

    private sealed class FakeAzureBlobStorageResource : FakeStorageChildResource
    {
        public FakeAzureBlobStorageResource(string name, IResource parent)
            : base(name, parent)
        {
        }
    }

    private sealed class FakeAzureTableStorageResource : FakeStorageChildResource
    {
        public FakeAzureTableStorageResource(string name, IResource parent)
            : base(name, parent)
        {
        }
    }

    /// <summary>
    /// A standalone Azure Service Bus namespace/emulator resource — owns its own endpoints (so the
    /// "emulator" endpoint reproduces the pre-fix Cosmos-fallback misclassification) and its
    /// type-name carries "ServiceBus" for the mesh to skip on.
    /// </summary>
    private sealed class FakeAzureServiceBusResource : Resource, IResourceWithConnectionString, IResourceWithEndpoints
    {
        public FakeAzureServiceBusResource(string name)
            : base(name)
        {
        }

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Endpoint=sb://localhost:5672;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true");
    }

    /// <summary>
    /// A child Service Bus queue — its type-name carries BOTH "ServiceBus" and "Queue", so it
    /// guards the ordering: the ServiceBus check must precede the Azurite Storage-queue check.
    /// </summary>
    private sealed class FakeAzureServiceBusQueueResource : FakeStorageChildResource
    {
        public FakeAzureServiceBusQueueResource(string name, IResource parent)
            : base(name, parent)
        {
        }
    }
}
