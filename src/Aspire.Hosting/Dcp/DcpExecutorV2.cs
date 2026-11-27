// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Runs an application using namespaced logical DCP V2 workloads, without the V1 IDE protocol.
/// </summary>
internal sealed class DcpExecutorV2(
    DistributedApplicationModel model,
    IKubernetesService kubernetesService,
    IDcpDependencyCheckService dependencyCheck,
    DcpNameGenerator nameGenerator,
    DcpEndpointAllocator endpointAllocator,
    DcpResourceLifecycle lifecycle,
    DcpAppResourceStore appResources,
    DcpWorkloadCreatorV2 creator,
    DcpExecutorEvents events,
    ResourceLoggerService loggerService,
    IConfiguration configuration,
    IOptions<DcpOptions> options,
    ILogger<DcpExecutorV2> logger) : IDcpExecutor, IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<DcpWorkloadV2> _workloads = [];
    private readonly List<DcpServiceV2> _services = [];
    private readonly V2.Namespace _namespace = V2.Namespace.Create(GetNamespaceName(model, configuration, options.Value));
    private DcpResourceWatcherV2? _watcher;
    private int _stopped;
    private bool _namespaceCreated;
    private bool _namespaceCollision;

    internal string NamespaceName => _namespace.Metadata.Name;
    internal DcpResourceWatcherV2? ResourceWatcher => _watcher;

    public async Task RunApplicationAsync(CancellationToken cancellationToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var ct = runCancellation.Token;
        var info = await dependencyCheck.GetDcpInfoAsync(cancellationToken: ct).ConfigureAwait(false);
        var containerHost = configuration["AppHost:ContainerHostname"] ??
            info?.Containers?.ContainerHostName ?? KnownHostNames.DockerDesktopHostBridge;

        PrepareServices();
        _workloads.AddRange(creator.Prepare(model, NamespaceName));
        appResources.AddRange(_workloads);
        PreparePorts();
        await events.PublishAsync(new OnResourcesPreparedContext(ct)).ConfigureAwait(false);

        try
        {
            // Probe the actual APIs, not a guessed release number. This also permits development builds.
            await ProbeAsync<V2.Namespace>(info, ct).ConfigureAwait(false);
            _namespace.Annotate(V2.Namespace.WorkloadIdAnnotation,
                configuration[KnownConfigNames.DcpWorkloadId] ??
                configuration["AppHost:Sha256"] ?? throw new DistributedApplicationException("AppHost identity is required for DCP V2."));
            try
            {
                var created = await kubernetesService.CreateAsync(_namespace, ct).ConfigureAwait(false);
                _namespace.Metadata = created.Metadata;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                _namespaceCollision = true;
                throw new DistributedApplicationException(
                    $"DCP V2 namespace '{NamespaceName}' already exists on this API server. Each AppHost run must use its own DCP API server; Aspire will not adopt or clean up another run's namespace.", ex);
            }
            _namespaceCreated = true;
            await WaitForAsync<V2.Namespace>([NamespaceName], n => n.Status?.Phase == V2.NamespacePhase.Active, ct).ConfigureAwait(false);
            await ProbeAsync<V2.LogicalService>(info, ct).ConfigureAwait(false);
            await ProbeAsync<V2.ServiceEndpoint>(info, ct).ConfigureAwait(false);
            await ProbeAsync<V2.LogicalProcess>(info, ct).ConfigureAwait(false);
            await ProbeAsync<V2.LogicalContainer>(info, ct).ConfigureAwait(false);
            await ProbeAsync<V2.LogicalContainerExec>(info, ct).ConfigureAwait(false);
            await ProbeAsync<V2.LogicalContainerNetwork>(info, ct).ConfigureAwait(false);
            await ProbeAsync<V2.PhysicalContainerVolume>(info, ct).ConfigureAwait(false);

            _watcher = new(kubernetesService, loggerService, events, _workloads, _services, NamespaceName, logger, _shutdown.Token);
            _watcher.Start();

            await Task.WhenAll(_services.SelectMany(s => s.HostService is { } host ? new[] { s.DcpResource, host } : [s.DcpResource])
                .Select(s => kubernetesService.CreateAsync(s, ct))).ConfigureAwait(false);

            var proxies = _services.SelectMany(s => s.HostService is { } host ? new[] { s.DcpResource, host } : [s.DcpResource])
                .Where(s => s.Spec.AddressAllocationMode != V2.ServiceAddressAllocationMode.Proxyless).ToArray();
            var observedServices = await WaitForAsync<V2.LogicalService>(
                proxies.Select(s => s.Metadata.Name),
                // Ready requires a published workload endpoint. Listener allocation precedes it.
                s => s.Status?.Phase != V2.LogicalServicePhase.Failed && SelectAddress(s.Status?.EffectiveAddresses) is not null,
                ct).ConfigureAwait(false);
            foreach (var service in proxies)
            {
                service.Status = observedServices[service.Metadata.Name].Status;
            }

            await PrepareInfrastructureAsync(ct).ConfigureAwait(false);
            AllocateEndpoints(containerHost);
            // Allocate the entire model before callbacks can resolve another resource's endpoint.
            await lifecycle.PublishEndpointsAllocatedAsync(
                _workloads.Select(w => w.ModelResource).Concat(_services.Select(s => s.ModelResource)), ct).ConfigureAwait(false);
            await CreateExternalEndpointsAsync(ct).ConfigureAwait(false);

            var processes = _workloads.Where(w => w.DcpResource is V2.LogicalProcess).ToArray();
            var containers = _workloads.Where(w => w.DcpResource is V2.LogicalContainer).ToArray();
            var execs = _workloads.Where(w => w.DcpResource is V2.LogicalContainerExec).ToArray();
            await Task.WhenAll(CreateWorkloadsAsync(processes, ct), CreateWorkloadsAsync(containers, ct),
                CreateWorkloadsAsync(execs, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            _shutdown.Cancel();
            throw;
        }
    }

    internal static string GetNamespaceName(DistributedApplicationModel model, IConfiguration configuration, DcpOptions options)
    {
        if (!model.GetProjectResources().Cast<IResource>().Concat(model.GetExecutableResources()).Any(r => !r.IsContainer() && r.HasPersistentLifetime()))
        {
            return $"aspire-{DcpNameGenerator.GetRandomNameSuffix()}";
        }
        var identity = configuration["AppHost:DeploymentStatePathSha256"] ??
            configuration["AppHost:FilePath"] ?? configuration["AppHost:Path"] ??
            configuration[KnownConfigNames.DcpWorkloadId] ??
            configuration["AppHost:PathSha256"] ?? configuration["AppHost:Sha256"] ??
            throw new DistributedApplicationException("A stable AppHost identity is required for persistent DCP V2 processes.");
        var hash = XxHash3.Hash(Encoding.UTF8.GetBytes($"{identity}/{options.ResourceNameSuffix}"));
        // DcpHost starts a dedicated API server using a fresh Locations.DcpKubeconfigPath for
        // each run. Stable names here share only DCP's persistent-process lease identity, not
        // API objects with another run. A conflicting create is never adopted.
        return $"aspire-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task ProbeAsync<T>(DcpInfo? info, CancellationToken cancellationToken) where T : CustomResource, IKubernetesStaticMetadata
    {
        try
        {
            await kubernetesService.ListAsync<T>(typeof(T) == typeof(V2.Namespace) ? null : NamespaceName, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw new DistributedApplicationException(
                $"DCP '{info?.VersionString ?? "unknown version"}' does not expose the required V2 {T.ObjectKind} API. " +
                "Use a DCP build with the logical V2 service/port APIs (DcpPublisher:CliPath), or disable DcpPublisher:UseV2. " +
                "A minimum released DCP version has not yet been established.", ex);
        }
    }

    private void PrepareServices()
    {
        var hasContainers = model.Resources.Any(r => r.IsContainer());
        foreach (var (resource, endpoints) in endpointAllocator.PrepareEndpoints(model))
        {
            for (var index = 0; index < endpoints.Length; index++)
            {
                var endpoint = endpoints[index];
                var (name, _) = nameGenerator.GetServiceName(resource, endpoint, endpoint.DefaultNetworkID);
                var service = V2.LogicalService.Create(name, NamespaceName);
                service.Spec.Protocol = PortProtocol.FromProtocolType(endpoint.Protocol);
                service.Spec.Port = endpointAllocator.AllocatePublicPort(resource, endpoint) ?? 0;
                service.Spec.Address = DcpModelUtilities.NormalizeTargetHost(endpoint.TargetHost).Item2 == EndpointBindingMode.SingleAddress
                    ? endpoint.TargetHost : null;
                service.Spec.AddressAllocationMode = endpoint.IsProxied
                    ? GetAllocationMode(endpoint.TargetHost)
                    : V2.ServiceAddressAllocationMode.Proxyless;
                service.Annotate(CustomResource.ResourceNameAnnotation, resource.Name);
                service.Annotate(CustomResource.EndpointNameAnnotation, endpoint.Name);
                var rendered = new DcpServiceV2(resource, endpoint, service, $"port-{index}");
                if (hasContainers && !resource.IsContainer())
                {
                    var (hostName, _) = nameGenerator.GetServiceName(resource, endpoint, KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
                    var host = V2.LogicalService.Create(hostName, NamespaceName);
                    host.Spec.Protocol = service.Spec.Protocol;
                    host.Spec.AddressAllocationMode = V2.ServiceAddressAllocationMode.AllInterfaces;
                    rendered.HostService = host;
                }
                _services.Add(rendered);
                appResources.Add(rendered);
            }
        }
    }

    internal static string GetAllocationMode(string host) => DcpModelUtilities.NormalizeTargetHost(host).Item2 switch
    {
        EndpointBindingMode.IPv4AnyAddresses => V2.ServiceAddressAllocationMode.IPv4AllInterfaces,
        EndpointBindingMode.IPv6AnyAddresses => V2.ServiceAddressAllocationMode.IPv6AllInterfaces,
        EndpointBindingMode.DualStackAnyAddresses => V2.ServiceAddressAllocationMode.AllInterfaces,
        _ => V2.ServiceAddressAllocationMode.Localhost
    };

    private void PreparePorts()
    {
        foreach (var workload in _workloads)
        {
            workload.Services.AddRange(_services.Where(s => ReferenceEquals(s.ModelResource, workload.ModelResource)));
            if (workload.DcpResource is V2.LogicalProcess process)
            {
                process.Spec.Ports = [];
                var replicas = _workloads.Count(w => ReferenceEquals(w.ModelResource, workload.ModelResource));
                foreach (var service in workload.Services)
                {
                    var endpoint = service.Endpoint;
                    if (replicas > 1 && (!endpoint.IsProxied || EndpointAnnotation.NormalizePort(endpoint.TargetPort) is not null))
                    {
                        throw new DistributedApplicationException($"Resource '{workload.ModelResource.Name}' has multiple replicas; its endpoints must be proxied and cannot have fixed target ports.");
                    }
                    if (endpoint.IsProxied && endpoint.TargetPort is int target && target == service.DcpResource.Spec.Port)
                    {
                        throw new DistributedApplicationException($"Proxied endpoint '{endpoint.Name}' on '{workload.ModelResource.Name}' cannot use the same public and target port.");
                    }
                    var port = endpointAllocator.AllocateExecutableTargetPort(workload.ModelResource, endpoint) ?? service.DcpResource.Spec.Port;
                    process.Spec.Ports.Add(new()
                    {
                        Name = service.PortName, ServiceRef = service.DcpResourceName,
                        Address = DcpModelUtilities.NormalizeTargetHost(endpoint.TargetHost).Item1, Port = port
                    });
                    if (service.HostService is { } host)
                    {
                        process.Spec.Ports.Add(new()
                        {
                            Name = $"host-{service.PortName}", ServiceRef = host.Metadata.Name,
                            Address = DcpModelUtilities.NormalizeTargetHost(endpoint.TargetHost).Item1, Port = port
                        });
                    }
                }
            }
            else if (workload.DcpResource is V2.LogicalContainer container)
            {
                container.Spec.Ports = workload.Services.Select(s => new V2.LogicalContainerPort
                {
                    Name = s.PortName, ServiceRef = s.DcpResourceName, ContainerPort = s.Endpoint.TargetPort!.Value,
                    HostPort = s.Endpoint.IsProxied ? 0 : s.DcpResource.Spec.Port,
                    HostIp = s.Endpoint.TargetHost == KnownHostNames.Localhost ? null : s.Endpoint.TargetHost
                }).ToList();
            }
        }
    }

    private async Task PrepareInfrastructureAsync(CancellationToken ct)
    {
        var containers = _workloads.Where(w => w.DcpResource is V2.LogicalContainer).ToArray();
        if (containers.Length == 0)
        {
            return;
        }
        var network = V2.LogicalContainerNetwork.Create(KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value, NamespaceName);
        var persistent = containers.Any(w => w.ModelResource.HasPersistentLifetime());
        network.Spec.Mode = persistent ? V2.LogicalContainerNetworkMode.Persistent : V2.LogicalContainerNetworkMode.Session;
        network.Spec.NetworkName = persistent
            ? $"aspire-persistent-network-{nameGenerator.GetProjectHashSuffix()}"
            : $"aspire-session-network-{NamespaceName}";
        network.Spec.Network = new();
        await kubernetesService.CreateAsync(network, ct).ConfigureAwait(false);
        await WaitForAsync<V2.LogicalContainerNetwork>([network.Metadata.Name],
            n => n.Status?.Phase == V2.LogicalContainerNetworkPhase.Ready, ct).ConfigureAwait(false);

        var volumes = containers.SelectMany(w => w.ModelResource.Annotations.OfType<ContainerMountAnnotation>()
            .Where(m => m.Type == ContainerMountType.Volume).Select(m =>
            {
                var name = DcpWorkloadCreatorV2.GetRuntimeVolumeName(w, m);
                var volume = V2.PhysicalContainerVolume.Create(DcpNameGenerator.GetContainerVolumeName(name), NamespaceName, name);
                volume.Spec.RemoveRuntimeVolumeOnDelete = string.IsNullOrEmpty(m.Source) && !w.ModelResource.HasPersistentLifetime();
                return volume;
            })).DistinctBy(v => v.Spec.VolumeName).ToArray();
        await Task.WhenAll(volumes.Select(v => kubernetesService.CreateAsync(v, ct))).ConfigureAwait(false);
        await WaitForAsync<V2.PhysicalContainerVolume>(volumes.Select(v => v.Metadata.Name),
            v => v.Status?.Phase == V2.PhysicalContainerVolumePhase.Ready, ct).ConfigureAwait(false);
    }

    private void AllocateEndpoints(string containerHost)
    {
        foreach (var service in _services)
        {
            var endpoint = service.Endpoint;
            var addresses = service.DcpResource.Status?.EffectiveAddresses;
            var address = SelectAddress(addresses);
            var port = address?.Port ?? service.DcpResource.Spec.Port;
            var expression = $$$"""{{- portForServing "{{{service.PortName}}}" -}}""";
            DcpEndpointAllocator.TryAllocateLocalhostEndpoint(endpoint, service.DcpResourceName,
                address?.Address ?? endpoint.TargetHost, port == 0 ? null : port,
                address is not null, expression, allowPending: false);
            if (service.ModelResource.IsContainer())
            {
                endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                    new(endpoint, $"{service.ModelResource.Name}.dev.internal", endpoint.TargetPort!.Value,
                        EndpointBindingMode.SingleAddress, expression, KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            }
            else if (service.HostService is { } host)
            {
                var hostAddress = SelectAddress(host.Status?.EffectiveAddresses) ??
                    throw new DistributedApplicationException($"Container-access service '{host.Metadata.Name}' has no address.");
                endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                    new(endpoint, containerHost, hostAddress.Port, EndpointBindingMode.SingleAddress,
                        expression, KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            }
        }
    }

    internal static V2.ServiceAddress? SelectAddress(List<V2.ServiceAddress>? addresses)
    {
        // effectiveAddresses can contain both families, e.g. [{"address":"127.0.0.1","port":5000},
        // {"address":"[::1]","port":5000}]. Prefer IPv4 for host-gateway compatibility, retaining
        // bracketed IPv6 when it is the only available family.
        return addresses?.FirstOrDefault(a => a.Port is > 0 and <= 65535 && !a.Address.Contains(':')) ??
            addresses?.FirstOrDefault(a => a.Port is > 0 and <= 65535 && !string.IsNullOrEmpty(a.Address));
    }

    private async Task CreateExternalEndpointsAsync(CancellationToken ct)
    {
        foreach (var service in _services.Where(s => !_workloads.Any(w => ReferenceEquals(w.ModelResource, s.ModelResource))))
        {
            var targetPort = service.Endpoint.TargetPort ?? service.Endpoint.Port;
            if (targetPort is null)
            {
                throw new DistributedApplicationException($"Endpoint '{service.Endpoint.Name}' on '{service.ModelResource.Name}' must specify a target port because it is not backed by a DCP workload.");
            }
            foreach (var target in service.HostService is { } host ? new[] { service.DcpResource, host } : [service.DcpResource])
            {
                var endpoint = V2.ServiceEndpoint.Create($"{target.Metadata.Name}-external", NamespaceName, target.Metadata.Name);
                endpoint.Spec.Addresses = [new()
                {
                    Address = DcpModelUtilities.NormalizeTargetHost(service.Endpoint.TargetHost).Item1, Port = targetPort.Value
                }];
                await kubernetesService.CreateAsync(endpoint, ct).ConfigureAwait(false);
            }
        }
    }

    private Task CreateWorkloadsAsync(DcpWorkloadV2[] workloads, CancellationToken ct) => lifecycle.CreateResourcesAsync(
        workloads, w => w.ResourceType,
        w => (!w.ExplicitStart || w.ModelResource.HasPersistentLifetime() && w.DcpResource is V2.LogicalContainer) &&
            (w.DcpResource is not V2.LogicalContainerExec exec ||
                !_workloads.Any(parent => parent.DcpResourceName == exec.Spec.ContainerRef && parent.ExplicitStart)),
        w => w.ExplicitStart,
        w => s => _watcher!.SnapshotBuilder.ToSnapshot(w.DcpResource, w.ModelResource, s),
        async (w, resourceLogger, token) =>
        {
            if (w.ExplicitStart && w.DcpResource is V2.LogicalContainer persistent)
            {
                // Existing mode observes a persistent runtime container without evaluating start-time
                // callbacks or creating a missing container. Manual start recreates it in persistent mode.
                persistent.Spec.Mode = V2.LogicalContainerMode.Existing;
                persistent.Spec.ContainerName = w.DcpResourceName;
                persistent.Spec.Container = null;
            }
            else
            {
                await creator.ConfigureAsync(w, resourceLogger, token).ConfigureAwait(false);
            }
            await CreateWorkloadAsync(w, token).ConfigureAwait(false);
        }, _shutdown.Token, ct);

    private async Task CreateWorkloadAsync(DcpWorkloadV2 workload, CancellationToken ct)
    {
        switch (workload.DcpResource)
        {
            case V2.LogicalProcess process:
                workload.DcpResource = await kubernetesService.CreateAsync(process, ct).ConfigureAwait(false);
                break;
            case V2.LogicalContainer container:
                workload.DcpResource = await kubernetesService.CreateAsync(container, ct).ConfigureAwait(false);
                break;
            case V2.LogicalContainerExec exec:
                var parent = _workloads.Single(w => w.DcpResource is V2.LogicalContainer && w.DcpResourceName == exec.Spec.ContainerRef);
                await parent.Initialized.WaitAsync(ct).ConfigureAwait(false);
                if (!parent.Created)
                {
                    throw new DistributedApplicationException($"Container '{parent.ModelResource.Name}' must be started before '{workload.ModelResource.Name}'.");
                }
                await WaitForAsync<V2.LogicalContainer>([parent.DcpResourceName],
                    c => c.Status?.Phase == V2.LogicalContainerPhase.Running, ct).ConfigureAwait(false);
                workload.DcpResource = await kubernetesService.CreateAsync(exec, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unexpected V2 workload '{workload.DcpResourceKind}'.");
        }
        workload.Created = true;
    }

    internal async Task<Dictionary<string, T>> WaitForAsync<T>(IEnumerable<string> names, Func<T, bool> ready, CancellationToken cancellationToken)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        var pending = names.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        if (pending.Count == 0)
        {
            return result;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            await foreach (var (eventType, resource) in kubernetesService.WatchAsync<T>(
                typeof(T) == typeof(V2.Namespace) ? null : NamespaceName, timeout.Token).ConfigureAwait(false))
            {
                if (eventType == WatchEventType.Bookmark || !pending.Contains(resource.Metadata.Name))
                {
                    continue;
                }
                var (phase, conditions) = resource switch
                {
                    V2.LogicalService s => (s.Status?.Phase, s.Status?.Conditions),
                    V2.LogicalContainerNetwork n => (n.Status?.Phase, n.Status?.Conditions),
                    V2.PhysicalContainerVolume v => (v.Status?.Phase, v.Status?.Conditions),
                    V2.LogicalContainer c => (c.Status?.Phase, c.Status?.Conditions),
                    _ => (null, null)
                };
                if (ready(resource))
                {
                    result[resource.Metadata.Name] = resource;
                    pending.Remove(resource.Metadata.Name);
                    if (pending.Count == 0)
                    {
                        return result;
                    }
                    continue;
                }
                if (eventType == WatchEventType.Deleted || phase is "Failed" or "Exited")
                {
                    throw new DistributedApplicationException(
                        $"DCP V2 {T.ObjectKind} '{resource.Metadata.Name}' did not become ready: {phase ?? "deleted"}. " +
                        string.Join("; ", conditions?.Select(c => $"{c.Reason}: {c.Message}") ?? []));
                }
            }
            throw new DistributedApplicationException($"DCP V2 {T.ObjectKind} watch ended before readiness: {string.Join(", ", pending)}.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DistributedApplicationException($"Timed out waiting for DCP V2 {T.ObjectKind}: {string.Join(", ", pending)}.", ex);
        }
    }

    public IResourceReference GetResource(string resourceName) => _workloads.FirstOrDefault(w =>
        string.Equals(w.DcpResourceName, resourceName, StringComparisons.ResourceName)) ??
        throw new InvalidOperationException($"Resource '{resourceName}' not found.");

    public async Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        var workload = (DcpWorkloadV2)resourceReference;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var ct = operationCancellation.Token;
        using var lease = await DcpResourceLifecycle.AcquireInitializedResourceAsync(workload, ct).ConfigureAwait(false);
        try
        {
            var children = _workloads.Where(w => w.DcpResource is V2.LogicalContainerExec exec &&
                exec.Spec.ContainerRef == workload.DcpResourceName).ToArray();
            foreach (var child in children.Where(w => w.Created))
            {
                using var childLease = await DcpResourceLifecycle.AcquireInitializedResourceAsync(child, ct).ConfigureAwait(false);
                await DeleteWorkloadAsync(child, ct).ConfigureAwait(false);
            }
            if (workload.Created)
            {
                await DeleteWorkloadAsync(workload, ct).ConfigureAwait(false);
            }
            lifecycle.ResetForRestart(workload.ModelResource);
            workload.ExplicitStart = false;
            await lifecycle.PublishConnectionStringAvailableAsync(workload.ModelResource, ct).ConfigureAwait(false);
            await events.PublishAsync(new OnResourceStartingContext(ct, workload.ResourceType, workload.ModelResource, workload.DcpResourceName)).ConfigureAwait(false);
            await creator.ConfigureAsync(workload, loggerService.GetLogger(workload.ModelResource), ct).ConfigureAwait(false);
            await CreateWorkloadAsync(workload, ct).ConfigureAwait(false);
            await lifecycle.PublishConnectionStringAvailableAsync(workload.ModelResource, ct).ConfigureAwait(false);
            foreach (var child in children.Where(w => !w.ExplicitStart))
            {
                await StartResourceAsync(child, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            loggerService.GetLogger(workload.ModelResource).LogError(ex, "Failed to start resource {ResourceName}", workload.ModelResource.Name);
            await events.PublishAsync(new OnResourceFailedToStartContext(
                ct, workload.ResourceType, workload.ModelResource, workload.DcpResourceName, ex.Message)).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        var workload = (DcpWorkloadV2)resourceReference;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var ct = operationCancellation.Token;
        using var lease = await DcpResourceLifecycle.AcquireInitializedResourceAsync(workload, ct).ConfigureAwait(false);
        if (!workload.Created)
        {
            return;
        }
        var patch = new V1Patch(JsonNode.Parse("""[{"op":"add","path":"/spec/stop","value":true}]"""), V1Patch.PatchType.JsonPatch);
        switch (workload.DcpResource)
        {
            case V2.LogicalProcess process:
                await kubernetesService.PatchAsync(process, patch, ct).ConfigureAwait(false);
                await WaitForAsync<V2.LogicalProcess>([workload.DcpResourceName], p => p.Status?.Phase is V2.LogicalProcessPhase.Exited or V2.LogicalProcessPhase.Failed, ct).ConfigureAwait(false);
                break;
            case V2.LogicalContainer container:
                await kubernetesService.PatchAsync(container, patch, ct).ConfigureAwait(false);
                await WaitForAsync<V2.LogicalContainer>([workload.DcpResourceName], c => c.Status?.Phase is V2.LogicalContainerPhase.Exited or V2.LogicalContainerPhase.Failed, ct).ConfigureAwait(false);
                break;
            case V2.LogicalContainerExec:
                var previous = _watcher!.GetObservedStatus(workload.DcpResource);
                await DeleteWorkloadAsync(workload, ct).ConfigureAwait(false);
                var stoppedAt = DateTime.UtcNow;
                await events.PublishAsync(new OnResourceChangedContext(ct, workload.ResourceType,
                    workload.ModelResource, workload.DcpResourceName,
                    new(KnownResourceStates.Exited, previous?.StartupTimestamp, stoppedAt), previous?.State,
                    snapshot => snapshot with
                    {
                        State = KnownResourceStates.Exited, ExitCode = null, StopTimeStamp = stoppedAt
                    })).ConfigureAwait(false);
                break;
        }
    }

    private async Task DeleteWorkloadAsync(DcpWorkloadV2 workload, CancellationToken ct)
    {
        // The old watch can report terminal state after the replacement starts. Retire the UID
        // before deletion, and do not hold the watcher's publication semaphore during a callback.
        _watcher!.Retire(workload.DcpResource);
        switch (workload.DcpResource)
        {
            case V2.LogicalProcess process:
                await DeleteAndWaitAsync(process, ct).ConfigureAwait(false);
                break;
            case V2.LogicalContainer container:
                await DeleteAndWaitAsync(container, ct).ConfigureAwait(false);
                break;
            case V2.LogicalContainerExec exec:
                await DeleteAndWaitAsync(exec, ct).ConfigureAwait(false);
                break;
        }
        var resource = (CustomResource)JsonSerializer.Deserialize(
            JsonSerializer.Serialize(workload.DcpResource, workload.DcpResource.GetType()), workload.DcpResource.GetType())!;
        resource.Metadata = new()
        {
            Name = workload.DcpResourceName, NamespaceProperty = NamespaceName,
            Annotations = resource.Metadata.Annotations
        };
        switch (resource)
        {
            case V2.LogicalProcess process:
                process.Status = null;
                process.Spec.Stop = false;
                break;
            case V2.LogicalContainer container:
                container.Status = null;
                container.Spec.Stop = false;
                break;
            case V2.LogicalContainerExec exec:
                exec.Status = null;
                break;
        }
        workload.DcpResource = resource;
        workload.Created = false;
    }

    private async Task DeleteAndWaitAsync<T>(T resource, CancellationToken cancellationToken)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        while (true)
        {
            try
            {
                await kubernetesService.DeleteAsync(resource, timeout.Token).ConfigureAwait(false);
                var remaining = await kubernetesService.GetAsync<T>(resource.Metadata.Name, NamespaceName, timeout.Token).ConfigureAwait(false);
                if (remaining.Metadata.Uid != resource.Metadata.Uid)
                {
                    throw new DistributedApplicationException($"DCP V2 resource '{resource.Metadata.Name}' was replaced by another owner during deletion.");
                }
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        Exception? monitoringFailure = null;
        if (_watcher is not null)
        {
            try
            {
                await _watcher.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                monitoringFailure = ex;
                logger.LogError(ex, "DCP V2 monitoring failed; cleaning up the owned namespace before propagating the failure.");
            }
        }
        if (_namespaceCreated)
        {
            try
            {
                // Namespace finalization deletes logical children before physical children. Persistent
                // container modes retain the runtime container; named volumes are not removed.
                await kubernetesService.DeleteAsync(_namespace, cancellationToken).ConfigureAwait(false);
                if (options.Value.WaitForResourceCleanup)
                {
                    while (true)
                    {
                        await kubernetesService.GetAsync<V2.Namespace>(NamespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                // Namespace finalization has completed.
            }
        }
        if (!_namespaceCollision)
        {
            await kubernetesService.StopServerAsync(ResourceCleanup.Full, cancellationToken).ConfigureAwait(false);
        }
        if (monitoringFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(monitoringFailure).Throw();
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await StopAsync(timeout.Token).ConfigureAwait(false);
        foreach (var workload in _workloads)
        {
            workload.Dispose();
        }
        _shutdown.Dispose();
    }
}
