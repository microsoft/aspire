// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Eventing;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Aspire.Hosting.Dcp;

internal sealed class DcpExecutor : IDcpExecutor, IDcpObjectFactory, IAsyncDisposable
{
    internal const string DebugSessionPortVar = "DEBUG_SESSION_PORT";

    // Disposal of the DcpExecutor means shutting down watches and log streams,
    // and asking DCP to start the shutdown process. If we cannot complete these tasks within 10 seconds,
    // it probably means DCP crashed and there is no point trying further.
    private static readonly TimeSpan s_disposeTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<DistributedApplication> _distributedApplicationLogger;
    private readonly IKubernetesService _kubernetesService;
    private readonly IConfiguration _configuration;
    private readonly ResourceLoggerService _loggerService;
    private readonly IDcpDependencyCheckService _dcpDependencyCheckService;
    private readonly DcpNameGenerator _nameGenerator;
    private readonly ILogger<DcpExecutor> _logger;
    private readonly DistributedApplicationModel _model;
    private readonly IDistributedApplicationEventing _distributedApplicationEventing;
    private readonly IOptions<DcpOptions> _options;
    private readonly DcpAppResourceStore _appResources;

    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly DcpExecutorEvents _executorEvents;
    private readonly DcpResourceWatcher _resourceWatcher;

    private readonly ExecutableCreator _executableCreator;
    private readonly ContainerCreator _containerCreator;
    private readonly ContainerNetworkEndpointProvisioner _containerNetworkEndpointProvisioner;
    private readonly DcpEndpointAllocator _endpointAllocator;
    private readonly DcpResourceLifecycle _resourceLifecycle;

    // Preserve the application-run endpoint context so explicit-start workloads use the same
    // container-network prerequisites and shared provisioning lifetime as initial workloads.
    private readonly TaskCompletionSource<ContainerNetworkEndpointContext> _containerNetworkEndpointContextSource;

    // Internal for testing.
    internal ResiliencePipeline<bool> DeleteResourceRetryPipeline { get; set; }

    private DcpInfo? _dcpInfo;
    private int _stopped;

    public DcpExecutor(ILogger<DcpExecutor> logger,
                       ILogger<DistributedApplication> distributedApplicationLogger,
                       DistributedApplicationModel model,
                       IKubernetesService kubernetesService,
                       IConfiguration configuration,
                       IDistributedApplicationEventing distributedApplicationEventing,
                       IOptions<DcpOptions> options,
                       ResourceLoggerService loggerService,
                       IDcpDependencyCheckService dcpDependencyCheckService,
                       DcpNameGenerator nameGenerator,
                        DcpExecutorEvents executorEvents,
                        DcpAppResourceStore appResources,
                        ExecutableCreator executableCreator,
                        ContainerCreator containerCreator,
                        ContainerNetworkEndpointProvisioner containerNetworkEndpointProvisioner,
                        ProfilingTelemetry profilingTelemetry,
                        DcpEndpointAllocator endpointAllocator,
                        DcpResourceLifecycle resourceLifecycle)
    {
        _distributedApplicationLogger = distributedApplicationLogger;
        _kubernetesService = kubernetesService;
        _configuration = configuration;
        _loggerService = loggerService;
        _dcpDependencyCheckService = dcpDependencyCheckService;
        _nameGenerator = nameGenerator;
        _executorEvents = executorEvents;
        _logger = logger;
        _model = model;
        _distributedApplicationEventing = distributedApplicationEventing;
        _options = options;
        _appResources = appResources;

        _resourceWatcher = new DcpResourceWatcher(logger, kubernetesService, loggerService, executorEvents, model, _appResources, profilingTelemetry, _shutdownCancellation.Token);

        DeleteResourceRetryPipeline = DcpPipelineBuilder.BuildDeleteRetryPipeline(logger);

        _containerNetworkEndpointContextSource = new TaskCompletionSource<ContainerNetworkEndpointContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        _executableCreator = executableCreator;
        _containerCreator = containerCreator;
        _containerNetworkEndpointProvisioner = containerNetworkEndpointProvisioner;
        _endpointAllocator = endpointAllocator;
        _resourceLifecycle = resourceLifecycle;
    }

    // Internal for testing.
    internal DcpResourceWatcher ResourceWatcher => _resourceWatcher;

    private string ContainerHostName => _configuration["AppHost:ContainerHostname"] ??
        (_options.Value.EnableAspireContainerTunnel ? KnownHostNames.DefaultContainerTunnelHostName : _dcpInfo?.Containers?.ContainerHostName ?? KnownHostNames.DockerDesktopHostBridge);

    public async Task RunApplicationAsync(CancellationToken ct = default)
    {
        using var activity = ProfilingTelemetry.StartDcpRunApplication(_configuration, _model.Resources.Count);

        // Container runtime details are only needed when the model has containers.
        _dcpInfo = _model.Resources.Any(r => r.IsContainer())
            ? await _dcpDependencyCheckService.GetDcpInfoAsync(cancellationToken: ct).ConfigureAwait(false)
            : await _dcpDependencyCheckService.GetDcpVersionAsync(ct).ConfigureAwait(false);

        Debug.Assert(_dcpInfo is not null, "DCP info should not be null at this point");

        // TODO: in the current Aspire implementation there a requirement that Executables and Containers backing Aspire resources
        // must be created only we created all AllocatedEndpoints these resource needed (e.g. for resolving environment variable values etc)
        // This is why we create objects in very specific order here.
        //
        // In future we should be able to make the model more flexible and streamline the DCP object creation logic by:
        // 1. Asynchronously publish AllocatedEndpoints as the Services associated with them transition to Ready state.
        // 2. Asynchronously create Executables and Containers as soon as all their dependencies are ready.

        try
        {
            _containerNetworkEndpointProvisioner.PrepareContainerNetwork();
            var containerVolumes = _containerCreator.PrepareContainerVolumes();

            using (var prepareServicesActivity = ProfilingTelemetry.StartDcpPrepareServices(_configuration))
            {
                try
                {
                    PrepareServices();
                }
                catch (Exception ex)
                {
                    prepareServicesActivity.SetError(ex);
                    throw;
                }
            }

            RenderedModelResource<Container>[] containers;
            RenderedModelResource<Executable>[] executables;
            using (var prepareResourcesActivity = ProfilingTelemetry.StartDcpPrepareResources(_configuration))
            {
                try
                {
                    containers = _containerCreator.PrepareObjects().ToArray();
                    _containerCreator.PrepareContainerExecutables();
                    executables = _executableCreator.PrepareObjects(ct).ToArray();
                    AllocateExecutableTargetPorts(executables);

                    prepareResourcesActivity.SetDcpPreparedResourceCounts(containers.Length, executables.Length);
                }
                catch (Exception ex)
                {
                    prepareResourcesActivity.SetError(ex);
                    throw;
                }
            }

            await _executorEvents.PublishAsync(new OnResourcesPreparedContext(ct)).ConfigureAwait(false);

            _resourceWatcher.Start();

            var createServices = Task.Run(() => CreateAllDcpObjectsAsync<Service>(ct), ct);

            var getProxyAddresses = Task.Run(async () =>
            {
                await createServices.ConfigureAwait(false);

                var proxiedWithNoAddress = _appResources.Get().OfType<AppResource<Service>>().Select(r => r.DcpResource)
                .Where(sr => !sr.HasCompleteAddress && sr.Spec.AddressAllocationMode != AddressAllocationModes.Proxyless);

                await UpdateWithEffectiveAddressInfo(proxiedWithNoAddress, ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }, ct);

            var createContainerNetworks = Task.Run(() => CreateAllDcpObjectsAsync<ContainerNetwork>(ct), ct);

            var createContainerVolumes = Task.Run(async () =>
            {
                await CreateDcpObjectsAsync(containerVolumes, ct).ConfigureAwait(false);
                var observedVolumes = await WaitForStateAsync(
                    containerVolumes,
                    volume => volume.Status?.State,
                    [ContainerVolumeState.Ready],
                    TimeSpan.FromMinutes(1),
                    ct).ConfigureAwait(false);
                EnsureContainerVolumesReady(observedVolumes);
            }, ct);

            var createWorkloadEndpoints = Task.Run(async () =>
            {
                await Task.WhenAll([getProxyAddresses, createContainerNetworks]).WaitAsync(ct).ConfigureAwait(false);

                List<IResource> endpointAllocatedResources = [];
                foreach (var executable in executables)
                {
                    if (DcpModelUtilities.TryAddWorkloadAllocatedEndpoints(
                        executable,
                        _options.Value.EnableAspireContainerTunnel,
                        ContainerHostName))
                    {
                        endpointAllocatedResources.Add(executable.ModelResource);
                    }
                }

                foreach (var container in containers)
                {
                    if (DcpModelUtilities.TryAddWorkloadAllocatedEndpoints(
                        container,
                        _options.Value.EnableAspireContainerTunnel,
                        ContainerHostName))
                    {
                        endpointAllocatedResources.Add(container.ModelResource);
                    }
                }

                await _resourceLifecycle.PublishEndpointsAllocatedAsync(endpointAllocatedResources, ct).ConfigureAwait(false);

            }, ct);

            // Container creation and executable configuration may both require endpoints expressed within the container network.
            var endpointContext = new ContainerNetworkEndpointContext(
                createContainerNetworks,
                createWorkloadEndpoints,
                _shutdownCancellation.Token);
            _containerNetworkEndpointContextSource.SetResult(endpointContext);

            var createExecutables = Task.Run(async () =>
            {
                await createWorkloadEndpoints.ConfigureAwait(false);

                await CreateRenderedResourcesAsync(_executableCreator, executables, endpointContext, ct).ConfigureAwait(false);
            }, ct);

            var createContainers = Task.Run(async () =>
            {
                await Task.WhenAll(createWorkloadEndpoints, createContainerVolumes).ConfigureAwait(false);

                await CreateRenderedResourcesAsync(_containerCreator, containers, endpointContext, ct).ConfigureAwait(false);
            }, ct);

            // Now wait for all "leaf" creations to complete.
            await Task.WhenAll(createExecutables, createContainers).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _shutdownCancellation.Cancel();
            _containerNetworkEndpointContextSource.TrySetException(ex);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _stopped, 1, 0) != 0)
        {
            return; // Already stopped/stop in progress.
        }

        _shutdownCancellation.Cancel();

        try
        {
            await _resourceWatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "One or more monitoring tasks terminated with an error.");
        }

        try
        {
            if (_options.Value.WaitForResourceCleanup)
            {
                await _kubernetesService.CleanupResourcesAsync(cancellationToken).ConfigureAwait(false);
            }

            // The app orchestrator (represented by kubernetesService here) will perform a resource cleanup
            // (if not done already) when the app host process exits.
            // This is just a perf optimization, so we do not care that much if this call fails.
            // There is not much difference for single app run, but for tests that tend to launch multiple instances
            // of app host from the same process, the gain from programmatic orchestrator shutdown is significant
            // See https://github.com/microsoft/aspire/issues/6561 for more info.
            await _kubernetesService.StopServerAsync(Model.ResourceCleanup.Full, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Application orchestrator could not be stopped programmatically.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var disposeCts = new CancellationTokenSource();
        disposeCts.CancelAfter(s_disposeTimeout);
        await StopAsync(disposeCts.Token).ConfigureAwait(false);
        foreach (var ar in _appResources.Get())
        {
            ar.Dispose();
        }
    }

    internal static string GetResourceType<T>(T resource, IResource appModelResource) where T : CustomResource
    {
        return resource switch
        {
            Container => KnownResourceTypes.Container,
            Executable => appModelResource.GetResourceType(),
            ContainerExec => KnownResourceTypes.ContainerExec,
            _ => throw new InvalidOperationException($"Unknown resource type {resource.GetType().Name}")
        };
    }

    Task IDcpObjectFactory.UpdateWithEffectiveAddressInfo(IEnumerable<Service> services, CancellationToken cancellationToken, TimeSpan? timeout)
        => UpdateWithEffectiveAddressInfo(services, cancellationToken, timeout);

    // Watches DCP object updates via a Kubernetes watch wrapped in the supplied retry pipeline,
    // till all objects reach desired state or a timeout occurs.
    // Returns names of objects that did not reach the desired state.
    private async Task<HashSet<string>> WatchUntilDesiredStateAsync<TDcpResource>(
        IEnumerable<TDcpResource> objects,
        Func<TDcpResource, TDcpResource, bool> isInDesiredState,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        var objectsByName = new Dictionary<string, TDcpResource>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);

        foreach (var o in objects)
        {
            var name = o.Metadata.Name;
            objectsByName[name] = o;
            pending.Add(name);
        }

        if (pending.Count == 0)
        {
            return pending;
        }

        try
        {
            await pipeline.ExecuteAsync(async (attemptCancellationToken) =>
            {
                // Note: a Kubernetes watch, when started, will return at least one event per existing object,
                // so we won't miss any state already present at the time the watch starts.
                var changeEnumerator = _kubernetesService.WatchAsync<TDcpResource>(cancellationToken: attemptCancellationToken);
                await foreach (var (evt, observed) in changeEnumerator.ConfigureAwait(false))
                {
                    if (evt == WatchEventType.Bookmark)
                    {
                        // Bookmarks do not contain any data.
                        continue;
                    }

                    if (!objectsByName.TryGetValue(observed.Metadata.Name, out var original))
                    {
                        // Not one of the objects we are tracking.
                        continue;
                    }

                    if (pending.Contains(observed.Metadata.Name) && isInDesiredState(original, observed))
                    {
                        pending.Remove(observed.Metadata.Name);
                    }

                    if (pending.Count == 0)
                    {
                        return; // We are done.
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException) { }

        // Best-effort final direct query for any still-pending objects in case the watch missed updates.
        foreach (var name in pending.ToArray())
        {
            var original = objectsByName[name];
            try
            {
                var fetched = await _kubernetesService.GetAsync<TDcpResource>(name, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (isInDesiredState(original, fetched))
                {
                    pending.Remove(name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to fetch latest state for {Kind} '{Name}' during DCP watch fallback.", original.Kind, name);
            }
        }

        return pending;
    }

    // Waits till provided set of Services have their addresses allocated by the orchestrator
    // and updates them with the allocated address information.
    private async Task UpdateWithEffectiveAddressInfo(IEnumerable<Service> services, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var needAddressAllocated = services.Where(s => !s.HasCompleteAddress).ToArray();
        if (needAddressAllocated.Length == 0)
        {
            return;
        }

        var createServicePipeline = DcpPipelineBuilder.BuildObjectWatchRetryPipeline(_options.Value, _logger, timeout);
        var initialServiceCount = needAddressAllocated.Length;
        HashSet<string> stillPending = [.. needAddressAllocated.Select(s => s.Metadata.Name)];
        using var activity = ProfilingTelemetry.StartDcpAllocateServiceAddresses(_configuration, initialServiceCount);

        try
        {
            stillPending = await WatchUntilDesiredStateAsync(
                needAddressAllocated,
                isInDesiredState: (original, observed) =>
                {
                    if (!observed.HasCompleteAddress)
                    {
                        return false;
                    }

                    original.ApplyAddressInfoFrom(observed);
                    activity.AddDcpServiceAddressAllocated(original.Metadata.Name);
                    return true;
                },
                createServicePipeline,
                cancellationToken).ConfigureAwait(false);

            // For services that still don't have an address, log a warning and emit a failure event.
            foreach (var sar in needAddressAllocated)
            {
                if (stillPending.Contains(sar.Metadata.Name))
                {
                    _distributedApplicationLogger.LogWarning("Unable to allocate a network port for service '{ServiceName}'; service may be unreachable and its clients may not work properly.", sar.Metadata.Name);
                    activity.AddDcpServiceAddressAllocationFailed(sar.Metadata.Name);
                }
            }

            if (_options.Value.EnableAspireContainerTunnel)
            {
                // Tunnel endpoints will be enabled (and get their endpoints) on as-needed basis. We are done for now.
                return;
            }

            // Container services are services that "mirror" their primary (host) service counterparts, but expose addresses usable from container network.
            // Without the tunnel we rely on Docker Desktop host.docker.internal bridge,
            // which means we just need to update their ports from primary services, changing the address to container host.
            var containerServices = _appResources.Get().OfType<AppResource<Service>>().Select(r => (
                Service: r.DcpResource,
                PrimaryServiceName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.PrimaryServiceNameAnnotation, out var psn) == true ? psn : null)
            )
            .Where(cs => !string.IsNullOrEmpty(cs.PrimaryServiceName) && cs.Service?.HasCompleteAddress is not true);

            foreach (var cs in containerServices)
            {
                var primaryService = _appResources.Get().OfType<ServiceWithModelResource>().Select(sar => sar.Service)
                    .First(svc => svc.Metadata.Name.Equals(cs.PrimaryServiceName));
                cs.Service!.ApplyAddressInfoFrom(primaryService);
                cs.Service!.Status!.EffectiveAddress = ContainerHostName;
            }
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
        finally
        {
            activity.SetDcpServiceAllocatedCount(initialServiceCount - stillPending.Count);
        }
    }

    internal static void EnsureContainerVolumesReady(IEnumerable<ContainerVolume> volumes)
    {
        var unreadyVolumes = volumes
            .Where(volume => !string.Equals(volume.Status?.State, ContainerVolumeState.Ready, StringComparison.Ordinal))
            .ToArray();
        if (unreadyVolumes.Length == 0)
        {
            return;
        }

        var details = string.Join(
            ", ",
            unreadyVolumes.Select(volume =>
                $"'{volume.Spec.Name ?? volume.Metadata.Name}': current state is '{volume.Status?.State ?? "(unknown)"}'"));
        throw new DistributedApplicationException($"One or more container volumes did not become ready: {details}");
    }

    // Waits until each provided object reports a state that is in finalStates, or until timeout elapses.
    // Returns the latest observed instance for each input object so callers can inspect Status.
    public async Task<IReadOnlyList<TDcpResource>> WaitForStateAsync<TDcpResource>(
        IEnumerable<TDcpResource> objects,
        Func<TDcpResource, string?> stateSelector,
        IReadOnlyCollection<string> finalStates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        // Latest observed instance per object name. Seeded with the inputs so that if no events arrive
        // we still return something meaningful (with whatever Status was on the input).
        var allItems = objects.ToArray();
        var latest = new Dictionary<string, TDcpResource>(StringComparer.Ordinal);
        foreach (var obj in allItems)
        {
            latest[obj.Metadata.Name] = obj;
        }

        var pending = allItems.Where(o => !IsInFinalState(stateSelector(o), finalStates)).ToArray();
        if (pending.Length > 0)
        {
            var pipeline = DcpPipelineBuilder.BuildObjectWatchRetryPipeline(_options.Value, _logger, timeout);

            await WatchUntilDesiredStateAsync(
                pending,
                isInDesiredState: (_, observed) =>
                {
                    latest[observed.Metadata.Name] = observed;
                    return IsInFinalState(stateSelector(observed), finalStates);
                },
                pipeline,
                cancellationToken).ConfigureAwait(false);
        }

        return latest.Values.ToArray();

        static bool IsInFinalState(string? state, IReadOnlyCollection<string> finalStates)
        {
            if (state is null)
            {
                return false;
            }

            return finalStates.Any(fs => string.Equals(state, fs, StringComparison.Ordinal));
        }
    }

    private Task CreateAllDcpObjectsAsync<RT>(CancellationToken cancellationToken) where RT : CustomResource, IKubernetesStaticMetadata
    {
        var objects = _appResources.Get().OfType<AppResource<RT>>().Select(ar => ar.DcpResource);
        return CreateDcpObjectsAsync(objects, cancellationToken);
    }

    Task IDcpObjectFactory.CreateDcpObjectsAsync<T>(IEnumerable<T> objects, CancellationToken cancellationToken)
        => CreateDcpObjectsAsync(objects, cancellationToken);

    Task<T> IDcpObjectFactory.PatchDcpObjectAsync<T>(T obj, Action<T> change, CancellationToken cancellationToken)
        => PatchDcpObjectAsync(obj, change, cancellationToken);

    private async Task<T> PatchDcpObjectAsync<T>(T obj, Action<T> change, CancellationToken cancellationToken)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        var patch = CreatePatch(obj, change);
        var result = await _kubernetesService.PatchAsync(obj, patch, cancellationToken).ConfigureAwait(false);
        change(obj);

        return result;
    }

    private async Task CreateDcpObjectsAsync<RT>(IEnumerable<RT> objects, CancellationToken cancellationToken) where RT : CustomResource, IKubernetesStaticMetadata
    {
        var toCreate = objects.ToImmutableArray();
        if (toCreate.Length == 0)
        {
            return;
        }

        try
        {
            using var currentActivity = ProfilingTelemetry.CurrentActivity(_configuration);
            var tasks = new List<Task>();
            foreach (var rtc in toCreate)
            {
                currentActivity.AnnotateTraceContext(rtc.Annotate);
                tasks.Add(Task.Run(async () =>
                {
                    await _kubernetesService.CreateAsync(rtc, cancellationToken).ConfigureAwait(false);
                }, cancellationToken));
            }
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // We catch and suppress the OperationCancelledException because the user may CTRL-C
            // during start up of the resources.
            _logger.LogDebug(ex, "Cancellation during creation of resources.");
        }
    }

    /// <summary>
    /// Creates DCP Service objects that represent services exposed by resources in the model via endpoints (EndpointAnnotations).
    /// </summary>
    private void PrepareServices()
    {
        var serviceProducers = _endpointAllocator.PrepareEndpoints(_model);

        // Create DCP services after known ports are excluded, allocating missing proxyless public ports as needed.
        foreach (var sp in serviceProducers)
        {
            var endpoints = sp.Endpoints;

            foreach (var endpoint in endpoints)
            {
                var (serviceName, isNew) = _nameGenerator.GetServiceName(sp.ModelResource, endpoint, endpoint.DefaultNetworkID);
                if (!isNew)
                {
                    _logger.LogWarning("Encountered the same service-endpoint combination more than once for {EndpointName} on resource {ResourceName} when creating default endpoint services. This should never happen.", endpoint.Name, sp.ModelResource.Name);
                    continue;
                }

                var svc = Service.Create(serviceName);

                svc.Spec.Port = _endpointAllocator.AllocatePublicPort(sp.ModelResource, endpoint);

                svc.Spec.Protocol = PortProtocol.FromProtocolType(endpoint.Protocol);
                if (string.Equals(KnownHostNames.Localhost, endpoint.TargetHost, StringComparison.OrdinalIgnoreCase))
                {
                    svc.Spec.Address = KnownHostNames.Localhost;
                }
                else
                {
                    svc.Spec.Address = endpoint.TargetHost;
                }

                if (!endpoint.IsProxied)
                {
                    svc.Spec.AddressAllocationMode = AddressAllocationModes.Proxyless;
                }

                // So we can associate the service with the resource that produced it and the endpoint it represents.
                svc.Annotate(CustomResource.ResourceNameAnnotation, sp.ModelResource.Name);
                svc.Annotate(CustomResource.EndpointNameAnnotation, endpoint.Name);

                var smr = new ServiceWithModelResource(sp.ModelResource, svc, endpoint);
                _appResources.Add(smr);
            }
        }

        var containers = _model.Resources.Where(r => r.IsContainer());
        if (!containers.Any())
        {
            return; // No container resources--no need bother with container-to-host connections.
        }

        if (_options.Value.EnableAspireContainerTunnel)
        {
            // Tunnel services and tunnel configuration is set up together with containers, dynamically.
            return;
        }

        // Legacy (no tunnel) mode: we are going to just proxy all host endpoint into the container network.
        var hostResources = _model.Resources.Select(HostResourceWithEndpoints.Create).OfType<HostResourceWithEndpoints>().ToList();

        foreach (var re in hostResources)
        {
            var containerNetworkServices = _containerNetworkEndpointProvisioner.CreateContainerNetworkServices(re);
            _appResources.AddRange(containerNetworkServices.Select(cns => cns.ServiceResource));
        }
    }

    private void AllocateExecutableTargetPorts(IEnumerable<RenderedModelResource<Executable>> executables)
    {
        // Allocate per rendered executable so replicas receive distinct target ports.
        foreach (var executable in executables)
        {
            if (!executable.DcpResource.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(
                CustomResource.ServiceProducerAnnotation,
                out var serviceProducerAnnotations))
            {
                continue;
            }

            var annotationsByServiceName = serviceProducerAnnotations.ToDictionary(a => a.ServiceName, StringComparer.Ordinal);
            var annotationsChanged = false;

            foreach (var serviceProducer in executable.ServicesProduced)
            {
                var endpoint = serviceProducer.EndpointAnnotation;
                if (!endpoint.IsProxied ||
                    EndpointAnnotation.NormalizePort(endpoint.TargetPort) is not null)
                {
                    continue;
                }

                var annotation = annotationsByServiceName[serviceProducer.Service.Metadata.Name];

                annotation.Port = _endpointAllocator.AllocateExecutableTargetPort(endpoint);
                annotationsChanged = true;
            }

            if (annotationsChanged)
            {
                // Annotation lists are deserialized copies, so persist the allocated ports back to the DCP resource.
                executable.DcpResource.SetAnnotationAsObjectList(
                    CustomResource.ServiceProducerAnnotation,
                    serviceProducerAnnotations);
            }
        }
    }

    public Task CreateRenderedResourcesAsync<TDcpResource, TContext>(
       IObjectCreator<TDcpResource, TContext> creator,
       IEnumerable<RenderedModelResource<TDcpResource>> resources,
       TContext context,
       CancellationToken cancellationToken)
       where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        return _resourceLifecycle.CreateResourcesAsync(
            resources,
            resource => GetResourceType(resource.DcpResource, resource.ModelResource),
            resource => creator.IsReadyToCreate(resource, context),
            resource => resource.DcpResource is Container { Spec.Start: false } or Executable { Spec.Start: false },
            resource => resource.DcpResource switch
            {
                Container container => s => _resourceWatcher.SnapshotBuilder.ToSnapshot(container, s),
                Executable exe => s => _resourceWatcher.SnapshotBuilder.ToSnapshot(exe, s),
                ContainerExec containerExec => s => _resourceWatcher.SnapshotBuilder.ToSnapshot(containerExec, s),
                _ => throw new NotImplementedException($"Does not support snapshots for resources of type '{resource.DcpResource.Kind}'")
            },
            (resource, logger, ct) => creator.CreateObjectAsync(resource, context, logger, this, ct),
            _shutdownCancellation.Token,
            cancellationToken);
    }

    /// <summary>
    /// Create a patch update using the specified resource.
    /// A copy is taken of the resource to avoid permanently changing it.
    /// </summary>
    private static V1Patch CreatePatch<T>(T obj, Action<T> change) where T : CustomResource
    {
        // This method isn't very efficient.
        // If mass or frequent patches are required then we may want to create patches manually.
        var current = JsonSerializer.SerializeToNode(obj);

        var copy = JsonSerializer.Deserialize<T>(current)!;
        change(copy);

        var changed = JsonSerializer.SerializeToNode(copy);

        var jsonPatch = JsonPatch.Create(current, changed);
        return new V1Patch(jsonPatch, V1Patch.PatchType.JsonPatch);
    }

    public IResourceReference GetResource(string resourceName)
    {
        var matchingResource = _appResources.Get()
            .Where(r => r.DcpResource is not Service)
            .Where(r => string.Equals(r.DcpResource.Metadata.Name, resourceName, StringComparisons.ResourceName))
            .OfType<IResourceReference>().FirstOrDefault();
        if (matchingResource is null)
        {
            throw new InvalidOperationException($"Resource '{resourceName}' not found.");
        }

        return matchingResource;
    }

    public async Task StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping resource '{ResourceName}'...", resourceReference.DcpResourceName);
        var appResource = (IAppResource)resourceReference;
        bool stopped = false;

        using var activity = ProfilingTelemetry.StartResourceStop(_configuration, resourceReference.ModelResource, appResource.DcpResourceKind, appResource.DcpResourceName);
        try
        {
            // No concurrent start/stop operations on the same resource. Must wait for initialization to complete.
            using var _ = await DcpResourceLifecycle.AcquireInitializedResourceAsync(appResource, cancellationToken).ConfigureAwait(false);

            stopped = await DeleteResourceRetryPipeline.ExecuteAsync(async (resourceName, attemptCancellationToken) =>
            {
                V1Patch patch;
                switch (appResource.DcpResource)
                {
                    case Container c:
                        patch = CreatePatch(c, obj => obj.Spec.Stop = true);
                        await _kubernetesService.PatchAsync(c, patch, attemptCancellationToken).ConfigureAwait(false);
                        var cu = await _kubernetesService.GetAsync<Container>(c.Metadata.Name, cancellationToken: attemptCancellationToken).ConfigureAwait(false);
                        // DCP reports FailedToStart (terminal) when the stop arrives before the container was launched.
                        if (cu.Status?.State is ContainerState.Exited or ContainerState.FailedToStart)
                        {
                            _logger.LogDebug("Container '{ResourceName}' was stopped.", resourceReference.DcpResourceName);
                            return true;
                        }
                        else
                        {
                            _logger.LogDebug("Container '{ResourceName}' is still running; trying again to stop it...", resourceReference.DcpResourceName);
                            return false;
                        }

                    case Executable e:
                        patch = CreatePatch(e, obj => obj.Spec.Stop = true);
                        await _kubernetesService.PatchAsync(e, patch, attemptCancellationToken).ConfigureAwait(false);
                        var eu = await _kubernetesService.GetAsync<Executable>(e.Metadata.Name, cancellationToken: attemptCancellationToken).ConfigureAwait(false);
                        // DCP reports FailedToStart (terminal) when the stop arrives before the process was launched,
                        // e.g. when a resource is stopped right after a restart re-created it.
                        if (eu.Status?.State is ExecutableState.Finished or ExecutableState.Terminated or ExecutableState.FailedToStart)
                        {
                            _logger.LogDebug("Executable '{ResourceName}' was stopped.", resourceReference.DcpResourceName);
                            return true;
                        }
                        else
                        {
                            _logger.LogDebug("Executable '{ResourceName}' is still running; trying again to stop it...", resourceReference.DcpResourceName);
                            return false;
                        }

                    default:
                        throw new InvalidOperationException($"Unexpected resource type: {appResource.DcpResourceKind}");
                }
            }, resourceReference.DcpResourceName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
        finally
        {
            activity.SetResourceStopped(stopped);
        }

        if (!stopped)
        {
            throw new InvalidOperationException($"Failed to stop resource '{resourceReference.DcpResourceName}'.");
        }
    }

    public async Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        var appResource = (IAppResource)resourceReference;
        var resourceType = GetResourceType(appResource.DcpResource, resourceReference.ModelResource);
        var resourceLogger = _loggerService.GetLogger(resourceReference.DcpResourceName);
        using var activity = ProfilingTelemetry.StartResourceStart(_configuration, resourceReference.ModelResource, appResource.DcpResourceKind, appResource.DcpResourceName, resourceType);

        try
        {
            _logger.LogDebug("Starting {ResourceType} '{ResourceName}'.", appResource.DcpResourceKind, resourceReference.DcpResourceName);

            // No concurrent start/stop operations on the same resource. Must wait for initialization to complete.
            using var _ = await DcpResourceLifecycle.AcquireInitializedResourceAsync(appResource, cancellationToken).ConfigureAwait(false);

            // For resources that need delete/recreate startup, raise the starting event after deletion. This is required because
            // deleting the existing DCP object temporarily overrides the status with a terminal state, such as "Exited".
            switch (resourceReference)
            {
                // We need to handle explicit start persistent resources specially on first launch as they may already be running, so we need to register them with DCP to discover their status.
                case RenderedModelResource<Container> { DcpResource.Spec.Start: false } cr when !DcpModelUtilities.ShouldDeferCreateForExplicitStart(cr.ModelResource, cr.DcpResource.Spec.Start):
                    await PublishConnectionStringAvailableEventAsync(cr.ModelResource, cancellationToken).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, cr.ModelResource, cr.DcpResourceName)).ConfigureAwait(false);
                    await PatchDcpObjectAsync(cr.DcpResource, static c => c.Spec.Start = true, cancellationToken).ConfigureAwait(false);
                    break;

                case RenderedModelResource<Executable> { DcpResource.Spec.Start: false } er when !DcpModelUtilities.ShouldDeferCreateForExplicitStart(er.ModelResource, er.DcpResource.Spec.Start):
                    await PublishConnectionStringAvailableEventAsync(er.ModelResource, cancellationToken).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, er.ModelResource, er.DcpResourceName)).ConfigureAwait(false);
                    await PatchDcpObjectAsync(er.DcpResource, static e => e.Spec.Start = true, cancellationToken).ConfigureAwait(false);
                    break;

                case RenderedModelResource<Container> cr:
                    await EnsureResourceDeletedAsync<Container>(resourceReference, cancellationToken).ConfigureAwait(false);

                    // Ensure we explicitly start the container even if original container was created in "delay-start" mode.
                    cr.DcpResource.Spec.Start = true;

                    await PublishConnectionStringAvailableEventAsync(resourceReference.ModelResource, cancellationToken).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName)).ConfigureAwait(false);
                    var containerEndpointContext = await _containerNetworkEndpointContextSource.Task.ConfigureAwait(false);
                    await _containerCreator.CreateObjectAsync(cr, containerEndpointContext, resourceLogger, this, cancellationToken).ConfigureAwait(false);
                    await PublishConnectionStringAvailableEventAsync(resourceReference.ModelResource, cancellationToken).ConfigureAwait(false);
                    break;
                case RenderedModelResource<Executable> er:
                    await EnsureResourceDeletedAsync<Executable>(resourceReference, cancellationToken).ConfigureAwait(false);

                    // Ensure we explicitly start the executable even if original executable was created in "delay-start" mode.
                    er.DcpResource.Spec.Start = true;

                    await PublishConnectionStringAvailableEventAsync(resourceReference.ModelResource, cancellationToken).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName)).ConfigureAwait(false);
                    var executableEndpointContext = await _containerNetworkEndpointContextSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    await _executableCreator.CreateObjectAsync(er, executableEndpointContext, resourceLogger, this, cancellationToken).ConfigureAwait(false);
                    await PublishConnectionStringAvailableEventAsync(resourceReference.ModelResource, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected resource type: {appResource.DcpResourceKind}");
            }
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            if (ex is not FailedToApplyEnvironmentException)
            {
                // FailedToApplyEnvironmentException is logged with actionable details where it is detected,
                // so avoid duplicating that entry with a generic stack trace.
                _logger.LogError(ex, "Failed to start resource {ResourceName}", resourceReference.ModelResource.Name);
            }

            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName, ex.Message)).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureResourceDeletedAsync<T>(IResourceReference resource, CancellationToken cancellationToken) where T : CustomResource, IKubernetesStaticMetadata
    {
        _logger.LogDebug("Ensuring '{ResourceName}' is deleted.", resource.DcpResourceName);

        // Reset cached callback results so they are re-evaluated on restart.
        _resourceLifecycle.ResetForRestart(resource.ModelResource);

        // The delete watch can clear its observation while the API deletion is still being polled.
        string? deletedUid = null;
        var result = await DeleteResourceRetryPipeline.ExecuteAsync(async (resourceName, attemptCancellationToken) =>
        {
            string? uid = null;

            // Make deletion part of the retry loop--we have seen cases during test execution when
            // the deletion request completed with success code, but it was never "acted upon" by DCP.

            try
            {
                var r = await _kubernetesService.DeleteAsync<T>(resourceName, cancellationToken: attemptCancellationToken).ConfigureAwait(false);
                uid = r.Uid();
                deletedUid = uid;

                _logger.LogDebug("Delete request for '{ResourceName}' successfully completed. Resource to delete has UID '{Uid}'.", resourceName, uid);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Delete request for '{ResourceName}' returned NotFound.", resourceName);

                // Not found means the resource is truly gone from the API server, which is our goal. Report success.
                return true;
            }

            // Ensure resource is deleted. DeleteAsync returns before the resource is completely deleted so we must poll
            // to discover when it is safe to recreate the resource. This is required because the resources share the same name.
            // Deleting a resource might take a while (more than 10 seconds), because DCP tries to gracefully shut it down first
            // before resorting to more extreme measures.

            try
            {
                _logger.LogDebug("Polling DCP to check if '{ResourceName}' is deleted...", resourceName);
                var r = await _kubernetesService.GetAsync<T>(resourceName, cancellationToken: attemptCancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Get request for '{ResourceName}' returned resource with UID '{Uid}'.", resourceName, uid);

                return false;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Get request for '{ResourceName}' returned NotFound.", resourceName);

                // Success.
                return true;
            }
        }, resource.DcpResourceName, cancellationToken).ConfigureAwait(false);

        if (!result)
        {
            throw new DistributedApplicationException($"Failed to delete '{resource.DcpResourceName}' successfully before restart.");
        }

        // Do not suppress updates from an object whose deletion failed.
        _resourceWatcher.MarkPreviousIncarnationSuperseded(T.ObjectKind, resource.DcpResourceName, deletedUid);
    }

    private Task PublishConnectionStringAvailableEventAsync(IResource resource, CancellationToken ct)
        => _resourceLifecycle.PublishConnectionStringAvailableAsync(resource, ct);
}
