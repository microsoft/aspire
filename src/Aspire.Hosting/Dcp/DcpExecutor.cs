// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Json.Patch;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Aspire.Hosting.Dcp;

internal sealed partial class DcpExecutor : IDcpExecutor, IDcpObjectFactory, IAsyncDisposable
{
    internal const string DebugSessionPortVar = "DEBUG_SESSION_PORT";

    // The base name for ephemeral container (Docker, Podman etc) networks
    internal const string DefaultAspireNetworkName = "aspire-session-network";

    // The base name for persistent container (Docker, Podman etc) networks
    internal const string DefaultAspirePersistentNetworkName = "aspire-persistent-network";

    // Disposal of the DcpExecutor means shutting down watches and log streams,
    // and asking DCP to start the shutdown process. If we cannot complete these tasks within 10 seconds,
    // it probably means DCP crashed and there is no point trying further.
    private static readonly TimeSpan s_disposeTimeout = TimeSpan.FromSeconds(10);

    // Regex for normalizing application names.
    [GeneratedRegex("""^(?<name>.+?)\.?AppHost$""", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationNameRegex();

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
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly DcpAppResourceStore _appResources;

    // Has an entry if we raised ResourceEndpointsAllocatedEvent for a resource with a given name.
    // We want to ensure we raise the event only once for each app model resource.
    // There may be multiple physical replicas of the same app model resource
    // which can result in the event being raised multiple times if we are not careful.
    private readonly HashSet<string> _endpointsAdvertised = new(StringComparers.ResourceName);

    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly DcpExecutorEvents _executorEvents;
    private readonly DcpResourceWatcher _resourceWatcher;

    private readonly ExecutableCreator _executableCreator;
    private readonly ContainerCreator _containerCreator;

    // We need to preserve the container creation context from the application startup phase 
    // so that container explicit start does not suffer from timing issues.
    private readonly TaskCompletionSource<ContainerCreationContext> _containerContextSource;

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
                       DistributedApplicationExecutionContext executionContext,
                       ResourceLoggerService loggerService,
                       IDcpDependencyCheckService dcpDependencyCheckService,
                       DcpNameGenerator nameGenerator,
                       DcpExecutorEvents executorEvents,
                       DcpAppResourceStore appResources,
                       ExecutableCreator executableCreator,
                       ContainerCreator containerCreator)
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
        _executionContext = executionContext;
        _appResources = appResources;

        _resourceWatcher = new DcpResourceWatcher(logger, kubernetesService, loggerService, executorEvents, model, _appResources, _shutdownCancellation.Token);

        DeleteResourceRetryPipeline = DcpPipelineBuilder.BuildDeleteRetryPipeline(logger);

        _containerContextSource = new TaskCompletionSource<ContainerCreationContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        _executableCreator = executableCreator;
        _containerCreator = containerCreator;
    }

    private string ContainerHostName => _configuration["AppHost:ContainerHostname"] ??
        (_options.Value.EnableAspireContainerTunnel ? KnownHostNames.DefaultContainerTunnelHostName : _dcpInfo?.Containers?.HostName ?? KnownHostNames.DockerDesktopHostBridge);

    public async Task RunApplicationAsync(CancellationToken ct = default)
    {
        using var activity = ProfilingTelemetry.StartDcpRunApplication(_model.Resources.Count);

        _dcpInfo = await _dcpDependencyCheckService.GetDcpInfoAsync(cancellationToken: ct).ConfigureAwait(false);

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
            AspireEventSource.Instance.DcpModelCreationStart();

            _containerCreator.PrepareContainerNetworks();

            AspireEventSource.Instance.DcpServiceObjectPreparationStart();
            try
            {
                using var prepareServicesActivity = ProfilingTelemetry.StartDcpPrepareServices();
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
            finally
            {
                AspireEventSource.Instance.DcpServiceObjectPreparationStop();
            }

            RenderedModelResource<Container>[] containers;
            RenderedModelResource<Executable>[] executables;
            using (var prepareResourcesActivity = ProfilingTelemetry.StartDcpPrepareResources())
            {
                try
                {
                    containers = _containerCreator.PrepareObjects().ToArray();
                    _containerCreator.PrepareContainerExecutables();
                    executables = _executableCreator.PrepareObjects().ToArray();

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

            var createExecutableEndpoints = Task.Run(async () =>
            {
                await getProxyAddresses.ConfigureAwait(false);

                AddAllocatedEndpointInfo(executables, AllocatedEndpointsMode.Workload);
                await PublishEndpointsAllocatedEventAsync(executables, ct).ConfigureAwait(false);
            }, ct);

            var createExecutables = Task.Run(async () =>
            {
                await createExecutableEndpoints.ConfigureAwait(false);

                await CreateRenderedResourcesAsync(_executableCreator, executables, EmptyCreationContext.s_instance, ct).ConfigureAwait(false);
            }, ct);

            Task createTunnelFunc(ContainerCreationContext cctx) => Task.Run(async () =>
            {
                await Task.WhenAll([getProxyAddresses, createContainerNetworks]).WaitAsync(ct).ConfigureAwait(false);

                await _containerCreator.CreateTunnelAsync(cctx, this, ct).ConfigureAwait(false);

                AddAllocatedEndpointInfo(executables, AllocatedEndpointsMode.ContainerTunnel);

                // createExecutableEndpoints() is not really part of container tunnel initialization,
                // but configuring containers that use the tunnel require these host network-side endpoints to be ready,
                // so instead of having container creation tasks wait on two separate tasks (current one + createExecutableEndpoints),
                // we just wait for createExecutableEndpoints here, and container creation tasks can then wait on this one.
                await createExecutableEndpoints.ConfigureAwait(false);
            }, ct);

            var cctx = new ContainerCreationContext(containers.Length, createTunnelFunc);
            _containerContextSource.SetResult(cctx);

            var createContainers = Task.Run(async () =>
            {
                await Task.WhenAll([getProxyAddresses, createContainerNetworks]).WaitAsync(ct).ConfigureAwait(false);

                // Allocate container workload endpoints, then publish endpoint-allocated events.
                AddAllocatedEndpointInfo(containers, AllocatedEndpointsMode.Workload);
                await PublishEndpointsAllocatedEventAsync(containers, ct).ConfigureAwait(false);

                await CreateRenderedResourcesAsync(_containerCreator, containers, cctx, ct).ConfigureAwait(false);
            }, ct);

            // Now wait for all "leaf" creations to complete.
            await Task.WhenAll(createExecutables, createContainers).WaitAsync(ct).ConfigureAwait(false);

            await _executorEvents.PublishAsync(new OnEndpointsAllocatedContext(ct)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _shutdownCancellation.Cancel();
            _containerContextSource.TrySetException(ex);
            throw;
        }
        finally
        {
            AspireEventSource.Instance.DcpModelCreationStop();
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
                try
                {
                    AspireEventSource.Instance.DcpResourceCleanupStart();
                    await _kubernetesService.CleanupResourcesAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    AspireEventSource.Instance.DcpResourceCleanupStop();
                }
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
        if (_containerContextSource.Task.IsCompletedSuccessfully)
        {
            _containerContextSource.Task.Result.Dispose();
        }
        foreach (var ar in _appResources.Get())
        {
            ar.Dispose();
        }
    }

    /// <summary>
    /// Normalizes the application name for use in physical container resource names (only guaranteed valid as a suffix).
    /// Removes the ".AppHost" suffix if present and takes only characters that are valid in resource names.
    /// Invalid characters are simply omitted from the name as the result doesn't need to be identical.
    /// </summary>
    /// <param name="applicationName">The application name to normalize.</param>
    /// <returns>The normalized application name with invalid characters removed.</returns>
    internal static string NormalizeApplicationName(string applicationName)
    {
        if (string.IsNullOrEmpty(applicationName))
        {
            return applicationName;
        }

        applicationName = ApplicationNameRegex().Match(applicationName) switch
        {
            Match { Success: true } match => match.Groups["name"].Value,
            _ => applicationName
        };

        if (string.IsNullOrEmpty(applicationName))
        {
            return applicationName;
        }

        var normalizedName = new StringBuilder();
        for (var i = 0; i < applicationName.Length; i++)
        {
            if ((applicationName[i] is >= 'a' and <= 'z') ||
                (applicationName[i] is >= 'A' and <= 'Z') ||
                (applicationName[i] is >= '0' and <= '9') ||
                (applicationName[i] is '_' or '-' or '.'))
            {
                normalizedName.Append(applicationName[i]);
            }
        }

        return normalizedName.ToString();
    }

    internal static string GetResourceType<T>(T resource, IResource appModelResource) where T : CustomResource
    {
        return resource switch
        {
            Container => KnownResourceTypes.Container,
            Executable => appModelResource is ProjectResource ? KnownResourceTypes.Project : KnownResourceTypes.Executable,
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
        using var activity = ProfilingTelemetry.StartDcpAllocateServiceAddresses(initialServiceCount);

        try
        {
            AspireEventSource.Instance.DcpServiceAddressAllocationStart(initialServiceCount);

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
                    AspireEventSource.Instance.DcpServiceAddressAllocated(original.Metadata.Name);
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
                    AspireEventSource.Instance.DcpServiceAddressAllocationFailed(sar.Metadata.Name);
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
            AspireEventSource.Instance.DcpServiceAddressAllocationStop(initialServiceCount - stillPending.Count);
            activity.SetDcpServiceAllocatedCount(initialServiceCount - stillPending.Count);
        }
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

    private async Task CreateDcpObjectsAsync<RT>(IEnumerable<RT> objects, CancellationToken cancellationToken) where RT : CustomResource, IKubernetesStaticMetadata
    {
        var toCreate = objects.ToImmutableArray();
        if (toCreate.Length == 0)
        {
            return;
        }

        using var activity = ProfilingTelemetry.StartDcpCreateObjects(RT.ObjectKind, toCreate.Length);

        AspireEventSource.Instance.DcpObjectSetCreationStart(RT.ObjectKind, toCreate.Length);
        try
        {
            var tasks = new List<Task>();
            foreach (var rtc in toCreate)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var createObjectActivity = ProfilingTelemetry.StartDcpCreateObject(rtc.Kind, rtc.Metadata.Name);
                        createObjectActivity.AnnotateTraceContext(rtc.Annotate);
                        AspireEventSource.Instance.DcpObjectCreationStart(rtc.Kind, rtc.Metadata.Name);
                        try
                        {
                            await _kubernetesService.CreateAsync(rtc, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            createObjectActivity.SetError(ex);
                            throw;
                        }
                    }
                    finally
                    {
                        AspireEventSource.Instance.DcpObjectCreationStop(rtc.Kind, rtc.Metadata.Name);
                    }

                }, cancellationToken));
            }
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            activity.SetError(ex);
            // We catch and suppress the OperationCancelledException because the user may CTRL-C
            // during start up of the resources.
            _logger.LogDebug(ex, "Cancellation during creation of resources.");
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
        finally
        {
            AspireEventSource.Instance.DcpObjectSetCreationStop(RT.ObjectKind, toCreate.Length);
        }
    }

    // Adds allocated endpoints for all relevant resources in the model
    private void AddAllocatedEndpointInfo<TDcpResource>(IEnumerable<RenderedModelResource<TDcpResource>> resources, AllocatedEndpointsMode mode)
            where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        foreach (var appResource in resources)
        {
            if ((mode & AllocatedEndpointsMode.Workload) != 0)
            {
                foreach (var sp in appResource.ServicesProduced)
                {
                    var svc = (Service)sp.DcpResource;

                    if (!svc.HasCompleteAddress && sp.EndpointAnnotation.IsProxied)
                    {
                        // This should never happen; if it does, we have a bug without a workaround for the user.
                        // We should have waited for the service to have a complete address before getting here.
                        throw new InvalidDataException($"Service {svc.Metadata.Name} should have valid address at this point");
                    }

                    if (!sp.EndpointAnnotation.IsProxied && svc.AllocatedPort is null)
                    {
                        throw new InvalidOperationException($"Service '{svc.Metadata.Name}' needs to specify a port for endpoint '{sp.EndpointAnnotation.Name}' since it isn't using a proxy.");
                    }

                    var (targetHost, bindingMode) = DcpModelUtilities.NormalizeTargetHost(sp.EndpointAnnotation.TargetHost);

                    sp.EndpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(
                        sp.EndpointAnnotation,
                        targetHost,
                        (int)svc.AllocatedPort!,
                        bindingMode,
                        targetPortExpression: $$$"""{{- portForServing "{{{svc.Metadata.Name}}}" -}}""",
                        KnownNetworkIdentifiers.LocalhostNetwork);

                    if (appResource.DcpResource is Container ctr && ctr.Spec.Networks is not null)
                    {
                        // Once container networks are fully supported, this should allocate endpoints on those networks
                        var containerNetwork = ctr.Spec.Networks.FirstOrDefault(n => n.Name == KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);

                        if (containerNetwork is not null)
                        {
                            var port = sp.EndpointAnnotation.TargetPort!;

                            var allocatedEndpoint = new AllocatedEndpoint(
                                sp.EndpointAnnotation,
                                $"{sp.ModelResource.Name}.dev.internal",
                                (int)port,
                                EndpointBindingMode.SingleAddress,
                                targetPortExpression: $$$"""{{- portForServing "{{{svc.Metadata.Name}}}" -}}""",
                                KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                            );
                            sp.EndpointAnnotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(allocatedEndpoint.NetworkID, allocatedEndpoint);
                        }
                    }

                    // If we are not using the tunnel, we can project Executable endpoints into container networks via ContainerHostName.
                    // This really only works for Docker Desktop, but it is useful for testing too.
                    if (appResource.DcpResource is Executable && !_options.Value.EnableAspireContainerTunnel)
                    {
                        var port = sp.EndpointAnnotation.TargetPort!;
                        var allocatedEndpoint = new AllocatedEndpoint(
                            sp.EndpointAnnotation,
                            ContainerHostName,
                            (int)svc.AllocatedPort!,
                            EndpointBindingMode.SingleAddress,
                            targetPortExpression: $$$"""{{- portForServing "{{{svc.Metadata.Name}}}" -}}""",
                            KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                        );
                        sp.EndpointAnnotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, allocatedEndpoint);
                    }
                }
            }

            if ((mode & AllocatedEndpointsMode.ContainerTunnel) != 0 && _options.Value.EnableAspireContainerTunnel)
            {
                // If there are any additional services that are not directly produced by this resource,
                // but leverage its endpoints via container tunnel, we want to add allocated endpoint info for them as well.

                var tunnelServices = _appResources.Get().OfType<AppResource<Service>>().Select(r => (
                    Service: r.DcpResource,
                    ResourceName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.ResourceNameAnnotation, out var resourceName) == true ? resourceName : null,
                    EndpointName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.EndpointNameAnnotation, out var endpointName) == true ? endpointName : null,
                    TunnelInstanceName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.ContainerTunnelInstanceName, out var tunnelInstanceName) == true ? tunnelInstanceName : null,
                    ContainerNetworkName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.ContainerNetworkAnnotation, out var containerNetworkName) == true ? containerNetworkName : null
                ))
                .Where(ts =>
                    ts.Service is not null &&
                    string.Equals(ts.ResourceName, appResource.ModelResource.Name, StringComparisons.ResourceName) &&
                    !string.IsNullOrEmpty(ts.EndpointName) &&
                    !string.IsNullOrEmpty(ts.ContainerNetworkName)
                );

                foreach (var ts in tunnelServices)
                {
                    if (!TryGetEndpoint(appResource.ModelResource, ts.EndpointName, out var endpoint))
                    {
                        throw new InvalidDataException($"Service '{ts.Service!.Metadata.Name}' refers to endpoint '{ts.EndpointName}' that does not exist");
                    }

                    if (ts.Service?.HasCompleteAddress is not true)
                    {
                        // This should never happen; if it does, we have a bug without a workaround for the user.
                        throw new InvalidDataException($"Container tunnel service {ts.Service?.Metadata.Name} should have valid address at this point");
                    }

                    var serverSvc = _appResources.Get().OfType<ServiceWithModelResource>().FirstOrDefault(swr =>
                        string.Equals(swr.ModelResource.Name, ts.ResourceName, StringComparisons.ResourceName) &&
                        string.Equals(swr.EndpointAnnotation.Name, endpoint.Name, StringComparisons.EndpointAnnotationName)
                    );
                    if (serverSvc is null)
                    {
                        // Should never happen -- we should have created a Service for every endpoint exposed from a resource.
                        throw new InvalidDataException($"The '{endpoint.Name}' on resource '{ts.ResourceName}' should have an associated DCP Service resource already set up");
                    }

                    var networkID = new NetworkIdentifier(ts.ContainerNetworkName!);
                    var address = string.IsNullOrEmpty(ts.TunnelInstanceName) ? ContainerHostName : KnownHostNames.DefaultContainerTunnelHostName;
                    var port = _options.Value.EnableAspireContainerTunnel ? (int)ts.Service!.AllocatedPort! : serverSvc.EndpointAnnotation.AllocatedEndpoint!.Port;

                    var tunnelAllocatedEndpoint = new AllocatedEndpoint(
                        endpoint,
                        address,
                        port,
                        EndpointBindingMode.SingleAddress,
                        targetPortExpression: $$$"""{{- portForServing "{{{ts.Service.Name}}}" -}}""",
                        networkID
                    );
                    endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(networkID, tunnelAllocatedEndpoint);
                }
            }
        }

    }

    /// <summary>
    /// Creates DCP Service objects that represent services exposed by resources in the model via endpoints (EndpointAnnotations).
    /// </summary>
    private void PrepareServices()
    {
        _logger.LogDebug("Preparing services. Ports randomized: {RandomizePorts}", _options.Value.RandomizePorts);

        var serviceProducers = _model.Resources
            .Select(r => (ModelResource: r, Endpoints: r.Annotations.OfType<EndpointAnnotation>().ToArray()))
            .Where(sp => sp.Endpoints.Any());

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

                if (!sp.ModelResource.SupportsProxy())
                {
                    // If the resource shouldn't be proxied, we need to enforce that on the annotation
                    endpoint.IsProxied = false;
                }

                int? port;
                if (_options.Value.RandomizePorts && endpoint.IsProxied && endpoint.Port != null)
                {
                    port = null;
                    _logger.LogDebug("Randomizing port for {ServiceName}. Original port: {OriginalPort}", serviceName, endpoint.Port);
                }
                else
                {
                    port = endpoint.Port;
                }
                svc.Spec.Port = port;
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
            var containerNetworkServices = _containerCreator.CreateContainerNetworkServicesForHostResource(re);
            _appResources.AddRange(containerNetworkServices.Select(cns => cns.ServiceResource));
        }
    }

    internal static void SetInitialResourceState(IResource resource, IAnnotationHolder annotationHolder)
    {
        // Store the initial state of the resource
        if (resource.TryGetLastAnnotation<ResourceSnapshotAnnotation>(out var initial) &&
            initial.InitialSnapshot.State?.Text is string state && !string.IsNullOrEmpty(state))
        {
            annotationHolder.Annotate(CustomResource.ResourceStateAnnotation, state);
        }
    }

    public async Task CreateRenderedResourcesAsync<TDcpResource, TContext>(
       IObjectCreator<TDcpResource, TContext> creator,
       IEnumerable<RenderedModelResource<TDcpResource>> resources,
       TContext context,
       CancellationToken cancellationToken)
       where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        if (!resources.Any())
        {
            return;
        }
        var allResources = resources.ToArray();

        var allResourceKinds = allResources.Select(r => r.DcpResourceKind).Distinct();
        if (allResourceKinds.Count() != 1)
        {
            throw new ArgumentException($"All resources should be of the same kind when calling CreateRenderedResourcesAsync. Found resource kinds: {string.Join(", ", allResourceKinds)}");
        }

        var resourceKind = allResourceKinds.First();
        var tasks = new List<Task>();
        using var activity = ProfilingTelemetry.StartDcpCreateRenderedResources(resourceKind, allResources.Length);

        try
        {
            AspireEventSource.Instance.DcpObjectSetCreationStart(resourceKind, allResources.Length);
            foreach (var group in allResources.GroupBy(e => e.ModelResource))
            {
                var groupList = group.ToList();
                var groupKey = group.Key;
                tasks.Add(Task.Run(() => CreateResourceReplicasAsync(groupKey, groupList, creator, context, cancellationToken), cancellationToken));
            }

            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
        finally
        {
            AspireEventSource.Instance.DcpObjectSetCreationStop(resourceKind, allResources.Length);
        }
    }

    /// <summary>
    /// Creates DCP resource replicas for a single Aspire model resource, handling all lifecycle events uniformly.
    /// This is the unified creation path for all resource types (Executable, Container, ContainerExec).
    /// </summary>
    private async Task CreateResourceReplicasAsync<TDcpResource, TContext>(
        IResource modelResource,
        IEnumerable<RenderedModelResource<TDcpResource>> replicaResources,
        IObjectCreator<TDcpResource, TContext> creator,
        TContext context,
        CancellationToken cancellationToken)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        var resourceLogger = _loggerService.GetLogger(modelResource);
        var resourceType = GetResourceType(replicaResources.First().DcpResource, modelResource);
        Debug.Assert(replicaResources.Any());
        var replicas = replicaResources.ToArray();
        using var activity = ProfilingTelemetry.StartResourceCreate(modelResource, resourceType, replicas.Length);

        try
        {
            // No concurrent start/stop operations on the same resource.
            using var _ = await ConcurrencyUtils.AcquireAllAsync(replicas.Select(r => r.SerializedOpSemaphore), cancellationToken).ConfigureAwait(false);

            // Publish snapshots built from DCP resources. Do this now to populate more values from DCP (source) to ensure they're
            // available if the resource isn't immediately started because it's waiting or is configured for explicit start.
            foreach (var r in replicas)
            {
                var snapshotBuild = BuildSnapshotFunc(r.DcpResource);

                await _executorEvents.PublishAsync(new OnResourceChangedContext(
                    _shutdownCancellation.Token, resourceType, modelResource,
                    r.DcpResourceName, new ResourceStatus(null, null, null),
                    snapshotBuild)
                ).ConfigureAwait(false);
            }

            // Note: DcpExecutor contract allows SOME replicas to be ready while others are not,
            // but the Aspire model does not allow this today.

            var allReady = replicas.All(r => creator.IsReadyToCreate(r, context));
            if (!allReady)
            {
                // Resource uses explicit startup and is not ready to create yet.
                // Publish NotStarted state; the resource will be created later via StartResourceAsync.
                foreach (var r in replicas)
                {
                    await _executorEvents.PublishAsync(new OnResourceChangedContext(
                        cancellationToken, resourceType, modelResource,
                        r.DcpResource.Metadata.Name,
                        new ResourceStatus(KnownResourceStates.NotStarted, null, null),
                        s => s with
                        {
                            State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, null)
                        })
                    ).ConfigureAwait(false);
                }
                return;
            }

            await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, modelResource)).ConfigureAwait(false);

            // For single-replica resources (e.g. containers), include the DCP resource name in the starting event.
            // For multi-replica resources (e.g. projects with replicas), the starting event applies to the group, so DcpResourceName is null.
            var startingDcpName = replicas.Length == 1 ? replicas[0].DcpResourceName : null;
            await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, modelResource, startingDcpName)).ConfigureAwait(false);

            foreach (var er in replicas)
            {
                try
                {
                    using var replicaActivity = ProfilingTelemetry.StartDcpCreateResourceReplica(er.ModelResource, er.DcpResourceKind, er.DcpResourceName);
                    AspireEventSource.Instance.DcpObjectCreationStart(er.DcpResourceKind, er.DcpResourceName);
                    try
                    {
                        await creator.CreateObjectAsync(er, context, resourceLogger, this, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        replicaActivity.SetError(ex);
                        throw;
                    }
                    finally
                    {
                        AspireEventSource.Instance.DcpObjectCreationStop(er.DcpResourceKind, er.DcpResourceName);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (FailedToApplyEnvironmentException)
                {
                    await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, er.ModelResource, er.DcpResource.Metadata.Name)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    resourceLogger.LogError(ex, "Failed to create resource {ResourceName}", er.ModelResource.Name);
                    await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, er.ModelResource, er.DcpResource.Metadata.Name)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            resourceLogger.LogError(ex, "Failed to create resource {ResourceName}", modelResource.Name);
            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, modelResource, DcpResourceName: null)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var r in replicas)
            {
                r.MarkInitialized();
            }
        }

        Func<CustomResourceSnapshot, CustomResourceSnapshot> BuildSnapshotFunc(CustomResource dcpResource)
        {
            return dcpResource switch
            {
                Container container => s => _resourceWatcher.SnapshotBuilder.ToSnapshot(container, s),
                Executable exe => s => _resourceWatcher.SnapshotBuilder.ToSnapshot(exe, s),
                ContainerExec containerExec => s => _resourceWatcher.SnapshotBuilder.ToSnapshot(containerExec, s),
                _ => throw new NotImplementedException($"Does not support snapshots for resources of type '{dcpResource.Kind}'")
            };
        }
    }

    /// <summary>
    /// Gets information about the resource's DCP instance. ReplicaInstancesAnnotation is added in BeforeStartEvent.
    /// </summary>
    internal static DcpInstance GetDcpInstance(IResource resource, int instanceIndex)
    {
        if (!resource.TryGetInstances(out var instances))
        {
            throw new DistributedApplicationException($"Couldn't find required {nameof(DcpInstancesAnnotation)} annotation on resource {resource.Name}.");
        }

        foreach (var instance in instances)
        {
            if (instance.Index == instanceIndex)
            {
                return instance;
            }
        }

        throw new DistributedApplicationException($"Couldn't find required instance ID for index {instanceIndex} on resource {resource.Name}.");
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

        var jsonPatch = current.CreatePatch(changed);
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

        using var activity = ProfilingTelemetry.StartResourceStop(resourceReference.ModelResource, appResource.DcpResourceKind, appResource.DcpResourceName);
        AspireEventSource.Instance.StopResourceStart(appResource.DcpResourceKind, appResource.DcpResourceName);
        try
        {
            // No concurrent start/stop operations on the same resource. Must wait for initialization to complete.
            await appResource.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var _ = await ConcurrencyUtils.AcquireAllAsync([appResource.SerializedOpSemaphore], cancellationToken).ConfigureAwait(false);

            stopped = await DeleteResourceRetryPipeline.ExecuteAsync(async (resourceName, attemptCancellationToken) =>
            {
                V1Patch patch;
                switch (appResource.DcpResource)
                {
                    case Container c:
                        patch = CreatePatch(c, obj => obj.Spec.Stop = true);
                        await _kubernetesService.PatchAsync(c, patch, attemptCancellationToken).ConfigureAwait(false);
                        var cu = await _kubernetesService.GetAsync<Container>(c.Metadata.Name, cancellationToken: attemptCancellationToken).ConfigureAwait(false);
                        if (cu.Status?.State == ContainerState.Exited)
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
                        if (eu.Status?.State == ExecutableState.Finished || eu.Status?.State == ExecutableState.Terminated)
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
            AspireEventSource.Instance.StopResourceStop(appResource.DcpResourceKind, appResource.DcpResourceName);
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
        using var activity = ProfilingTelemetry.StartResourceStart(resourceReference.ModelResource, appResource.DcpResourceKind, appResource.DcpResourceName, resourceType);
        AspireEventSource.Instance.StartResourceStart(appResource.DcpResourceKind, appResource.DcpResourceName);

        try
        {
            _logger.LogDebug("Starting {ResourceType} '{ResourceName}'.", appResource.DcpResourceKind, resourceReference.DcpResourceName);

            // No concurrent start/stop operations on the same resource. Must wait for initialization to complete.
            await appResource.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var _ = await ConcurrencyUtils.AcquireAllAsync([appResource.SerializedOpSemaphore], cancellationToken).ConfigureAwait(false);

            // Reset cached callback results so they are re-evaluated on restart.
            ForgetCachedCallbackResults(resourceReference.ModelResource);

            // Raise event after resource has been deleted. This is required because the event sets the status to "Starting" and resources being
            // deleted will temporarily override the status to a terminal state, such as "Exited".
            switch (resourceReference)
            {
                case RenderedModelResource<Container> cr:
                    await EnsureResourceDeletedAsync<Container>(resourceReference.DcpResourceName, cancellationToken).ConfigureAwait(false);

                    // Ensure we explicitly start the container even if original container was created in "delay-start" mode.
                    cr.DcpResource.Spec.Start = true;

                    await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, resourceReference.ModelResource)).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName)).ConfigureAwait(false);
                    var startupCctx = await _containerContextSource.Task.ConfigureAwait(false);
                    using (var cctx = startupCctx.ForAdditionalContainers(1))
                    {
                        await _containerCreator.CreateObjectAsync(cr, cctx, resourceLogger, this, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case RenderedModelResource<Executable> er:
                    await EnsureResourceDeletedAsync<Executable>(resourceReference.DcpResourceName, cancellationToken).ConfigureAwait(false);

                    await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, resourceReference.ModelResource)).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName)).ConfigureAwait(false);
                    await _executableCreator.CreateObjectAsync(er, EmptyCreationContext.s_instance, resourceLogger, this, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected resource type: {appResource.DcpResourceKind}");
            }
        }
        catch (FailedToApplyEnvironmentException)
        {
            // For this exception we don't want the noise of the stack trace, we've already
            // provided more detail where we detected the issue (e.g. envvar name). To get
            // more diagnostic information reduce logging level for DCP log category to Debug.
            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "Failed to start resource {ResourceName}", resourceReference.ModelResource.Name);
            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, resourceReference.ModelResource, resourceReference.DcpResourceName)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            AspireEventSource.Instance.StartResourceStop(appResource.DcpResourceKind, resourceReference.DcpResourceName);
        }
    }

    private async Task EnsureResourceDeletedAsync<T>(string resourceName, CancellationToken cancellationToken) where T : CustomResource, IKubernetesStaticMetadata
    {
        _logger.LogDebug("Ensuring '{ResourceName}' is deleted.", resourceName);

        var result = await DeleteResourceRetryPipeline.ExecuteAsync(async (resourceName, attemptCancellationToken) =>
        {
            string? uid = null;

            // Make deletion part of the retry loop--we have seen cases during test execution when
            // the deletion request completed with success code, but it was never "acted upon" by DCP.

            try
            {
                var r = await _kubernetesService.DeleteAsync<T>(resourceName, cancellationToken: attemptCancellationToken).ConfigureAwait(false);
                uid = r.Uid();

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
        }, resourceName, cancellationToken).ConfigureAwait(false);

        if (!result)
        {
            throw new DistributedApplicationException($"Failed to delete '{resourceName}' successfully before restart.");
        }
    }

    private static bool TryGetEndpoint(IResource resource, string? endpointName, [NotNullWhen(true)] out EndpointAnnotation? endpoint)
    {
        endpoint = null;
        if (resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            endpoint = endpoints.FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparisons.EndpointAnnotationName));
        }
        return endpoint is not null;
    }

    /// <summary>
    /// Clears cached callback results on resource annotations so they are re-evaluated on restart.
    /// </summary>
    private static void ForgetCachedCallbackResults(IResource resource)
    {
        if (resource.TryGetEnvironmentVariables(out var envCallbacks))
        {
            foreach (var callback in envCallbacks)
            {
                ((ICallbackResourceAnnotation<EnvironmentCallbackContext, Dictionary<string, object>>)callback).ForgetCachedResult();
            }
        }

        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbacks))
        {
            foreach (var callback in argsCallbacks)
            {
                ((ICallbackResourceAnnotation<CommandLineArgsCallbackContext, IList<object>>)callback).ForgetCachedResult();
            }
        }
    }

    private async Task PublishEndpointsAllocatedEventAsync<TDcpResource>(IEnumerable<RenderedModelResource<TDcpResource>> resource, CancellationToken ct)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        foreach (var r in resource)
        {
            lock (_endpointsAdvertised)
            {
                if (!_endpointsAdvertised.Add(r.ModelResource.Name))
                {
                    continue; // Already published for this resource
                }
            }

            var ev = new ResourceEndpointsAllocatedEvent(r.ModelResource, _executionContext.ServiceProvider);
            await _distributedApplicationEventing.PublishAsync(ev, EventDispatchBehavior.NonBlockingConcurrent, ct).ConfigureAwait(false);
        }
    }
}
