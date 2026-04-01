// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Aspire.Shared.ConsoleLogs;
using Json.Patch;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Aspire.Hosting.Dcp;

internal sealed partial class DcpExecutor : IDcpExecutor, IConsoleLogsService, IAsyncDisposable
{
    internal const string DebugSessionPortVar = "DEBUG_SESSION_PORT";

    // The base name for ephemeral container (Docker, Pdman etc) networks
    internal const string DefaultAspireNetworkName = "aspire-session-network";

    // The base name for persistent container (Docker, Pdman etc) networks
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
    private readonly DistributedApplicationOptions _distributedApplicationOptions;
    private readonly IDistributedApplicationEventing _distributedApplicationEventing;
    private readonly IOptions<DcpOptions> _options;
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly ConcurrentBag<AppResource> _appResources = [];

    // Has an entry if we raised ResourceEndpointsAllocatedEvent for a resource with a given name.
    // We want to ensure we raise the event only once for each app model resource.
    // There may be multiple physical replicas of the same app model resource
    // which can result in the event being raised multiple times if we are not careful.
    private readonly HashSet<string> _endpointsAdvertised = new(StringComparers.ResourceName);

    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly DcpExecutorEvents _executorEvents;
    private readonly Locations _locations;
    private readonly DcpResourceState _resourceState;
    private readonly ResourceSnapshotBuilder _snapshotBuilder;

    private readonly string _normalizedApplicationName;

    // Internal for testing.
    internal ResiliencePipeline<bool> DeleteResourceRetryPipeline { get; set; }
    internal ResiliencePipeline WatchResourceRetryPipeline { get; set; }

    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Task)> _logStreams = new();
    private DcpInfo? _dcpInfo;
    private Task? _resourceWatchTask;
    private int _stopped;

    private readonly record struct LogInformationEntry(string ResourceName, bool? LogsAvailable, bool? HasSubscribers);
    private readonly Channel<LogInformationEntry> _logInformationChannel = Channel.CreateUnbounded<LogInformationEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    public DcpExecutor(ILogger<DcpExecutor> logger,
                       ILogger<DistributedApplication> distributedApplicationLogger,
                       DistributedApplicationModel model,
                       IHostEnvironment hostEnvironment,
                       IKubernetesService kubernetesService,
                       IConfiguration configuration,
                       IDistributedApplicationEventing distributedApplicationEventing,
                       DistributedApplicationOptions distributedApplicationOptions,
                       IOptions<DcpOptions> options,
                       DistributedApplicationExecutionContext executionContext,
                       ResourceLoggerService loggerService,
                       IDcpDependencyCheckService dcpDependencyCheckService,
                       DcpNameGenerator nameGenerator,
                       DcpExecutorEvents executorEvents,
                       Locations locations)
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
        _distributedApplicationOptions = distributedApplicationOptions;
        _options = options;
        _executionContext = executionContext;
        _resourceState = new(model.Resources.ToDictionary(r => r.Name), _appResources);
        _snapshotBuilder = new(_resourceState);
        _normalizedApplicationName = NormalizeApplicationName(hostEnvironment.ApplicationName);
        _locations = locations;

        DeleteResourceRetryPipeline = DcpPipelineBuilder.BuildDeleteRetryPipeline(logger);
        WatchResourceRetryPipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(logger);
    }

    private string ContainerHostName => _configuration["AppHost:ContainerHostname"] ??
        (_options.Value.EnableAspireContainerTunnel ? KnownHostNames.DefaultContainerTunnelHostName : _dcpInfo?.Containers?.HostName ?? KnownHostNames.DockerDesktopHostBridge);

    public async Task RunApplicationAsync(CancellationToken ct = default)
    {
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

            PrepareContainerNetworks();

            AspireEventSource.Instance.DcpServiceObjectPreparationStart();
            try
            {
                PrepareServices();
            }
            finally
            {
                AspireEventSource.Instance.DcpServiceObjectPreparationStop();
            }

            PrepareContainers();
            PrepareExecutables();

            await _executorEvents.PublishAsync(new OnResourcesPreparedContext(ct)).ConfigureAwait(false);

            WatchResourceChanges();

            var createServices = Task.Run(() => CreateAllDcpObjectsAsync<Service>(ct), ct);

            var getProxyAddresses = Task.Run(async () =>
            {
                await createServices.ConfigureAwait(false);

                var proxiedWithNoAddress = _appResources.Where(r => r.DcpResource is Service { }).Select(r => (Service)r.DcpResource)
                .Where(sr => !sr.HasCompleteAddress && sr.Spec.AddressAllocationMode != AddressAllocationModes.Proxyless);

                await UpdateWithEffectiveAddressInfo(proxiedWithNoAddress, ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }, ct);

            var createContainerNetworks = Task.Run(() => CreateAllDcpObjectsAsync<ContainerNetwork>(ct), ct);

            var executables = _appResources.OfType<RenderedModelResource>().Where(ar => ar.DcpResource is Executable).ToArray();
            var containers = _appResources.OfType<RenderedModelResource>().Where(ar => ar.DcpResource is Container).ToArray();

            var createExecutableEndpoints = Task.Run(async () =>
            {
                await getProxyAddresses.ConfigureAwait(false);

                AddAllocatedEndpointInfo(executables, AllocatedEndpointsMode.Workload);
                await PublishEndpointAllocatedEventAsync(executables, ct).ConfigureAwait(false);
            }, ct);

            var createExecutables = Task.Run(async () =>
            {
                await createExecutableEndpoints.ConfigureAwait(false);

                await CreateExecutablesAsync(executables, ct).ConfigureAwait(false);
            }, ct);

            Task createTunnelFunc(ContainerCreationContext cctx) => Task.Run(async () =>
            {
                await Task.WhenAll([getProxyAddresses, createContainerNetworks]).WaitAsync(ct).ConfigureAwait(false);

                // Container creation tasks need to figure out dependencies of each container
                // and then create Service and TunnelConfiguration definitions for each of them.
                cctx.ContainerServicesSpecReady.Wait(ct);
                cctx.ContainerServicesChan.Writer.Complete();

                // Now create the container network services for the host resources, update the tunnel, and advertise AllocatedEndpoints.
                var containerNetworkServices = cctx.ContainerServicesChan.Reader.ReadAllAsync(ct).ToBlockingEnumerable(ct).ToArray();
                _appResources.AddRange(containerNetworkServices.Select(cns => cns.ServiceResource));
                var serviceObjects = containerNetworkServices.Select(cns => cns.ServiceResource.Service).ToArray();
                await CreateDcpObjectsAsync(serviceObjects, ct).ConfigureAwait(false);

                var tunnels = containerNetworkServices.Where(s => s.TunnelConfig is not null).Select(s => s.TunnelConfig!).ToList();
                Debug.Assert(tunnels.Count == containerNetworkServices.Length, "Each tunneled service should have a tunnel config");
                var tunnelAppResource = CreateTunnelProxyResource(tunnels);
                var tunnelProxy = (ContainerNetworkTunnelProxy)tunnelAppResource.DcpResource;
                await CreateAllDcpObjectsAsync<ContainerNetworkTunnelProxy>(ct).ConfigureAwait(false);

                // Container tunnel initialization can take a while if the container tunnel image needs to be built,
                // especially if the required image pull is slow, hence 10 minute timeout here.
                await UpdateWithEffectiveAddressInfo(serviceObjects, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);

                AddAllocatedEndpointInfo(executables, AllocatedEndpointsMode.ContainerTunnel);

                // createExecutableEndpoints() is not really part of container tunnel initialization,
                // but configuring containers that use the tunnel require these host network-side endpoints to be ready,
                // so instead of having container creation tasks wait on two separate tasks (current one + createExecutableEndpoints),
                // we just wait for createExecutableEndpoints here, and container creation tasks can then wait on this one.
                await createExecutableEndpoints.ConfigureAwait(false);
            }, ct);

            using var cctx = new ContainerCreationContext(containers.Length, createTunnelFunc);

            var createContainers = Task.Run(async () =>
            {
                await Task.WhenAll([getProxyAddresses, createContainerNetworks]).WaitAsync(ct).ConfigureAwait(false);

                await Task.WhenAll(containers.Select(c => Task.Run(() => CreateSingleContainerAsync(c, cctx, ct)))).WaitAsync(ct).ConfigureAwait(false);
            }, ct);

            // Now wait for all "leaf" creations to complete.
            await Task.WhenAll(createExecutables, createContainers).WaitAsync(ct).ConfigureAwait(false);

            await _executorEvents.PublishAsync(new OnEndpointsAllocatedContext(ct)).ConfigureAwait(false);
        }
        catch
        {
            _shutdownCancellation.Cancel();
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
        var tasks = new List<Task>();
        if (_resourceWatchTask is { } resourceTask)
        {
            tasks.Add(resourceTask);
        }

        foreach (var (_, (cancellation, logTask)) in _logStreams)
        {
            cancellation.Cancel();
            tasks.Add(logTask);
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
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
    }

    private void WatchResourceChanges()
    {
        var outputSemaphore = new SemaphoreSlim(1);

        var cancellationToken = _shutdownCancellation.Token;
        var watchResourcesTask = Task.Run(async () =>
        {
            using (outputSemaphore)
            {
                await Task.WhenAll(
                    Task.Run(() => WatchKubernetesResourceAsync<Executable>((t, r) => ProcessResourceChange(t, r, _resourceState.ExecutablesMap, Model.Dcp.ExecutableKind, (e, s) => _snapshotBuilder.ToSnapshot(e, s)))),
                    Task.Run(() => WatchKubernetesResourceAsync<Container>((t, r) => ProcessResourceChange(t, r, _resourceState.ContainersMap, Model.Dcp.ContainerKind, (c, s) => _snapshotBuilder.ToSnapshot(c, s)))),
                    Task.Run(() => WatchKubernetesResourceAsync<ContainerExec>((t, r) => ProcessResourceChange(t, r, _resourceState.ContainerExecsMap, Model.Dcp.ContainerExecKind, (c, s) => _snapshotBuilder.ToSnapshot(c, s)))),
                    Task.Run(() => WatchKubernetesResourceAsync<Service>(ProcessServiceChange)),
                    Task.Run(() => WatchKubernetesResourceAsync<Endpoint>(ProcessEndpointChange))).ConfigureAwait(false);
            }
        });

        _loggerService.SetConsoleLogsService(this);

        var watchSubscribersTask = Task.Run(async () =>
        {
            await foreach (var subscribers in _loggerService.WatchAnySubscribersAsync(cancellationToken).ConfigureAwait(false))
            {
                _logInformationChannel.Writer.TryWrite(new(subscribers.Name, LogsAvailable: null, subscribers.AnySubscribers));
            }
        });

        // Listen to the "log information channel" - which contains updates when resources have logs available and when they have subscribers.
        // A resource needs both logs available and subscribers before it starts streaming its logs.
        // We only want to start the log stream for resources when they have subscribers.
        // And when there are no more subscribers, we want to stop the stream.
        var watchInformationChannelTask = Task.Run(async () =>
        {
            var resourceLogState = new Dictionary<string, (bool logsAvailable, bool hasSubscribers)>();

            await foreach (var entry in _logInformationChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var logsAvailable = false;
                var hasSubscribers = false;
                if (resourceLogState.TryGetValue(entry.ResourceName, out (bool, bool) stateEntry))
                {
                    (logsAvailable, hasSubscribers) = stateEntry;
                }

                // LogsAvailable can only go from false => true. Once it is true, it can never go back to false.
                Debug.Assert(!entry.LogsAvailable.HasValue || entry.LogsAvailable.Value, "entry.LogsAvailable should never be 'false'");

                logsAvailable = entry.LogsAvailable ?? logsAvailable;
                hasSubscribers = entry.HasSubscribers ?? hasSubscribers;

                if (logsAvailable)
                {
                    if (hasSubscribers)
                    {
                        if (_resourceState.ContainersMap.TryGetValue(entry.ResourceName, out var container))
                        {
                            StartLogStream(container);
                        }
                        else if (_resourceState.ExecutablesMap.TryGetValue(entry.ResourceName, out var executable))
                        {
                            StartLogStream(executable);
                        }
                        else if (_resourceState.ContainerExecsMap.TryGetValue(entry.ResourceName, out var containerExec))
                        {
                            StartLogStream(containerExec);
                        }
                    }
                    else
                    {
                        if (_logStreams.TryRemove(entry.ResourceName, out var logStream))
                        {
                            logStream.Cancellation.Cancel();
                        }
                    }
                }

                resourceLogState[entry.ResourceName] = (logsAvailable, hasSubscribers);
            }
        });

        _resourceWatchTask = Task.WhenAll(watchResourcesTask, watchSubscribersTask, watchInformationChannelTask);

        async Task WatchKubernetesResourceAsync<T>(Func<WatchEventType, T, Task> handler) where T : CustomResource, IKubernetesStaticMetadata
        {
            try
            {
                _logger.LogDebug("Watching over DCP {ResourceType} resources.", typeof(T).Name);
                await WatchResourceRetryPipeline.ExecuteAsync(async (pipelineCancellationToken) =>
                {
                    await foreach (var (eventType, resource) in _kubernetesService.WatchAsync<T>(cancellationToken: pipelineCancellationToken).ConfigureAwait<(global::k8s.WatchEventType, T)>(false))
                    {
                        await outputSemaphore.WaitAsync(pipelineCancellationToken).ConfigureAwait(false);

                        try
                        {
                            await handler(eventType, resource).ConfigureAwait(false);
                        }
                        finally
                        {
                            outputSemaphore.Release();
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown requested.
                _logger.LogDebug("Cancellation received while watching {ResourceType} resources.", typeof(T).Name);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Watch task over Kubernetes {ResourceType} resources terminated unexpectedly.", typeof(T).Name);
            }
            finally
            {
                _logger.LogDebug("Stopped watching {ResourceType} resources.", typeof(T).Name);
            }
        }
    }

    private async Task ProcessResourceChange<T>(WatchEventType watchEventType, T resource, ConcurrentDictionary<string, T> resourceByName, string resourceKind, Func<T, CustomResourceSnapshot, CustomResourceSnapshot> snapshotFactory) where T : CustomResource
    {
        if (ProcessResourceChange(resourceByName, watchEventType, resource))
        {
            UpdateAssociatedServicesMap();

            var changeType = watchEventType switch
            {
                WatchEventType.Added or WatchEventType.Modified => ResourceSnapshotChangeType.Upsert,
                WatchEventType.Deleted => ResourceSnapshotChangeType.Delete,
                _ => throw new System.ComponentModel.InvalidEnumArgumentException($"Cannot convert {nameof(WatchEventType)} with value {watchEventType} into enum of type {nameof(ResourceSnapshotChangeType)}.")
            };

            // Find the associated application model resource and update it.
            var resourceName = resource.AppModelResourceName;

            if (resourceName is not null &&
                _resourceState.ApplicationModel.TryGetValue(resourceName, out var appModelResource))
            {
                if (changeType == ResourceSnapshotChangeType.Delete)
                {
                    // Stop the log stream for the resource
                    if (_logStreams.TryRemove(resource.Metadata.Name, out var logStream))
                    {
                        logStream.Cancellation.Cancel();
                    }

                    // TODO: Handle resource deletion
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Deleting application model resource {ResourceName} with {ResourceKind} resource {ResourceName}", appModelResource.Name, resourceKind, resource.Metadata.Name);
                    }
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Updating application model resource {ResourceName} with {ResourceKind} resource {ResourceName}", appModelResource.Name, resourceKind, resource.Metadata.Name);
                    }

                    var resourceType = GetResourceType(resource, appModelResource);
                    var status = GetResourceStatus(resource);
                    await _executorEvents.PublishAsync(new OnResourceChangedContext(_shutdownCancellation.Token, resourceType, appModelResource, resource.Metadata.Name, status, s => snapshotFactory(resource, s))).ConfigureAwait(false);

                    if (resource is Container { LogsAvailable: true } ||
                        resource is Executable { LogsAvailable: true } ||
                        resource is ContainerExec { LogsAvailable: true })
                    {
                        _logInformationChannel.Writer.TryWrite(new(resource.Metadata.Name, LogsAvailable: true, HasSubscribers: null));
                    }
                }
            }
            else
            {
                // No application model resource found for the DCP resource.
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("No application model resource found for {ResourceKind} resource {ResourceName}", resourceKind, resource.Metadata.Name);
                }
            }
        }

        void UpdateAssociatedServicesMap()
        {
            // We keep track of associated services for the resource
            // So whenever we get the service we can figure out if the service can generate endpoint for the resource
            if (watchEventType == WatchEventType.Deleted)
            {
                _resourceState.ResourceAssociatedServicesMap.Remove((resourceKind, resource.Metadata.Name), out _);
            }
            else if (resource.Metadata.Annotations?.TryGetValue(CustomResource.ServiceProducerAnnotation, out var servicesProducedAnnotationJson) == true)
            {
                var serviceProducerAnnotations = JsonSerializer.Deserialize<ServiceProducerAnnotation[]>(servicesProducedAnnotationJson);
                if (serviceProducerAnnotations is not null)
                {
                    _resourceState.ResourceAssociatedServicesMap[(resourceKind, resource.Metadata.Name)]
                        = serviceProducerAnnotations.Select(e => e.ServiceName).ToList();
                }
            }
        }
    }

    /// <summary>
    /// Normalizes the application name for use in physical container resource names (only guaranteed valid as a suffix).
    /// Removes the ".AppHost" suffix if present and takes only characters that are valid in resource names.
    /// Invalid characters are simply omitted from the name as the result doesn't need to be identical.
    /// </summary>
    /// <param name="applicationName">The application name to normalize.</param>
    /// <returns>The normalized application name with invalid characters removed.</returns>
    private static string NormalizeApplicationName(string applicationName)
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

    private static string GetResourceType<T>(T resource, IResource appModelResource) where T : CustomResource
    {
        return resource switch
        {
            Container => KnownResourceTypes.Container,
            Executable => appModelResource is ProjectResource ? KnownResourceTypes.Project : KnownResourceTypes.Executable,
            ContainerExec => KnownResourceTypes.ContainerExec,
            _ => throw new InvalidOperationException($"Unknown resource type {resource.GetType().Name}")
        };
    }

    private static ResourceStatus GetResourceStatus(CustomResource resource)
    {
        if (resource is Container container)
        {
            if (container.Spec.Start == false && (container.Status?.State == null || container.Status?.State == ContainerState.Pending))
            {
                // If the resource is set for delay start, treat pending states as NotStarted.
                return new(KnownResourceStates.NotStarted, null, null);
            }

            return new(container.Status?.State, container.Status?.StartupTimestamp?.ToUniversalTime(), container.Status?.FinishTimestamp?.ToUniversalTime());
        }
        if (resource is Executable executable)
        {
            return new(executable.Status?.State, executable.Status?.StartupTimestamp?.ToUniversalTime(), executable.Status?.FinishTimestamp?.ToUniversalTime());
        }
        if (resource is ContainerExec containerExec)
        {
            return new(containerExec.Status?.State, containerExec.Status?.StartupTimestamp?.ToUniversalTime(), containerExec.Status?.FinishTimestamp?.ToUniversalTime());
        }

        return new(null, null, null);
    }

    public async IAsyncEnumerable<IReadOnlyList<LogEntry>> GetAllLogsAsync(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<IReadOnlyList<(string, bool)>>? enumerable = null;
        if (_resourceState.ContainersMap.TryGetValue(resourceName, out var container))
        {
            enumerable = new ResourceLogSource<Container>(_logger, _kubernetesService, container, follow: false);
        }
        else if (_resourceState.ExecutablesMap.TryGetValue(resourceName, out var executable))
        {
            enumerable = new ResourceLogSource<Executable>(_logger, _kubernetesService, executable, follow: false);
        }
        else if (_resourceState.ContainerExecsMap.TryGetValue(resourceName, out var containerExec))
        {
            enumerable = new ResourceLogSource<ContainerExec>(_logger, _kubernetesService, containerExec, follow: false);
        }

        if (enumerable != null)
        {
            await foreach (var batch in enumerable.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var logs = new List<LogEntry>();
                foreach (var logEntry in CreateLogEntries(batch))
                {
                    logs.Add(logEntry);
                }

                yield return logs;
            }
        }
    }

    private static IEnumerable<LogEntry> CreateLogEntries(IReadOnlyList<(string, bool)> batch)
    {
        foreach (var (content, logEntryType) in batch)
        {
            DateTime? timestamp = null;
            var resolvedContent = content;

            if (TimestampParser.TryParseConsoleTimestamp(resolvedContent, out var result))
            {
                resolvedContent = result.Value.ModifiedText;
                timestamp = result.Value.Timestamp.UtcDateTime;
            }

            yield return LogEntry.Create(timestamp, resolvedContent, content, logEntryType, resourcePrefix: null);
        }
    }

    private void StartLogStream<T>(T resource) where T : CustomResource, IKubernetesStaticMetadata
    {
        IAsyncEnumerable<IReadOnlyList<(string, bool)>>? enumerable = resource switch
        {
            Container c when c.LogsAvailable => new ResourceLogSource<T>(_logger, _kubernetesService, resource, follow: true),
            Executable e when e.LogsAvailable => new ResourceLogSource<T>(_logger, _kubernetesService, resource, follow: true),
            ContainerExec e when e.LogsAvailable => new ResourceLogSource<T>(_logger, _kubernetesService, resource, follow: true),
            _ => null
        };

        // No way to get logs for this resource as yet
        if (enumerable is null)
        {
            return;
        }

        // This does not run concurrently for the same resource so we can safely use GetOrAdd without
        // creating multiple log streams.
        _logStreams.GetOrAdd(resource.Metadata.Name, (_) =>
        {
            var cancellation = new CancellationTokenSource();

            var task = Task.Run(async () =>
            {
                try
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Starting log streaming for {ResourceName}.", resource.Metadata.Name);
                    }

                    // Pump the logs from the enumerable into the logger
                    var logger = _loggerService.GetInternalLogger(resource.Metadata.Name);

                    await foreach (var batch in enumerable.WithCancellation(cancellation.Token).ConfigureAwait(false))
                    {
                        foreach (var logEntry in CreateLogEntries(batch))
                        {
                            logger(logEntry);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                    _logger.LogDebug("Log streaming for {ResourceName} was cancelled.", resource.Metadata.Name);
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Resource was deleted — this is expected for short-lived resources like rebuilders.
                    _logger.LogDebug("Log streaming for {ResourceName} ended because the resource was deleted.", resource.Metadata.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error streaming logs for {ResourceName}.", resource.Metadata.Name);
                }
            },
            cancellation.Token);

            return (cancellation, task);
        });
    }

    private async Task ProcessEndpointChange(WatchEventType watchEventType, Endpoint endpoint)
    {
        if (!ProcessResourceChange(_resourceState.EndpointsMap, watchEventType, endpoint))
        {
            return;
        }

        if (endpoint.Metadata.OwnerReferences is null)
        {
            return;
        }

        foreach (var ownerReference in endpoint.Metadata.OwnerReferences)
        {
            await TryRefreshResource(ownerReference.Kind, ownerReference.Name).ConfigureAwait(false);
        }
    }

    private async Task ProcessServiceChange(WatchEventType watchEventType, Service service)
    {
        if (!ProcessResourceChange(_resourceState.ServicesMap, watchEventType, service))
        {
            return;
        }

        foreach (var ((resourceKind, resourceName), _) in _resourceState.ResourceAssociatedServicesMap.Where(e => e.Value.Contains(service.Metadata.Name)))
        {
            await TryRefreshResource(resourceKind, resourceName).ConfigureAwait(false);
        }
    }

    private async ValueTask TryRefreshResource(string resourceKind, string resourceName)
    {
        CustomResource? cr = resourceKind switch
        {
            "Container" => _resourceState.ContainersMap.TryGetValue(resourceName, out var container) ? container : null,
            "ContainerExec" => _resourceState.ContainerExecsMap.TryGetValue(resourceName, out var containerExec) ? containerExec : null,
            "Executable" => _resourceState.ExecutablesMap.TryGetValue(resourceName, out var executable) ? executable : null,
            _ => null
        };

        if (cr is not null)
        {
            var appModelResourceName = cr.AppModelResourceName;

            if (appModelResourceName is not null &&
                _resourceState.ApplicationModel.TryGetValue(appModelResourceName, out var appModelResource))
            {
                var status = GetResourceStatus(cr);
                await _executorEvents.PublishAsync(new OnResourceChangedContext(_shutdownCancellation.Token, resourceKind, appModelResource, resourceName, status, s =>
                {
                    if (cr is Container container)
                    {
                        return _snapshotBuilder.ToSnapshot(container, s);
                    }
                    else if (cr is Executable exe)
                    {
                        return _snapshotBuilder.ToSnapshot(exe, s);
                    }
                    else if (cr is ContainerExec containerExec)
                    {
                        return _snapshotBuilder.ToSnapshot(containerExec, s);
                    }
                    return s;
                })).ConfigureAwait(false);
            }
        }
    }

    private static bool ProcessResourceChange<T>(ConcurrentDictionary<string, T> map, WatchEventType watchEventType, T resource)
            where T : CustomResource
    {
        switch (watchEventType)
        {
            case WatchEventType.Added:
                map.TryAdd(resource.Metadata.Name, resource);
                break;

            case WatchEventType.Modified:
                map[resource.Metadata.Name] = resource;
                break;

            case WatchEventType.Deleted:
                map.Remove(resource.Metadata.Name, out _);
                break;

            default:
                return false;
        }

        return true;
    }

    // Waits till provided set of Services have their addresses allocated by the orchestrator
    // and updates them with the allocated address information.
    private async Task UpdateWithEffectiveAddressInfo(IEnumerable<Service> services, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        List<Service> needAddressAllocated = new(services.Where(s => !s.HasCompleteAddress));
        if (needAddressAllocated.Count == 0)
        {
            return;
        }

        var createServicePipeline = DcpPipelineBuilder.BuildCreateServiceRetryPipeline(_options.Value, _logger, timeout);
        var initialServiceCount = needAddressAllocated.Count;

        try
        {
            AspireEventSource.Instance.DcpServiceAddressAllocationStart(initialServiceCount);

            await createServicePipeline.ExecuteAsync(async (attemptCancellationToken) =>
            {
                // Note: a Kubernetes watch, when started, will return at least one event per existing object, so we won't miss any service state.
                var serviceChangeEnumerator = _kubernetesService.WatchAsync<Service>(cancellationToken: attemptCancellationToken);
                await foreach (var (evt, updated) in serviceChangeEnumerator.ConfigureAwait(false))
                {
                    if (evt == WatchEventType.Bookmark)
                    {
                        // Bookmarks do not contain any data.
                        continue;
                    }

                    var srvResource = needAddressAllocated.FirstOrDefault(sr => sr.Metadata.Name == updated.Metadata.Name);
                    if (srvResource == null)
                    {
                        // This service most likely already has full address information, so it is not on needAddressAllocated list.
                        continue;
                    }

                    if (updated.HasCompleteAddress)
                    {
                        srvResource.ApplyAddressInfoFrom(updated);
                        needAddressAllocated.Remove(srvResource);
                        AspireEventSource.Instance.DcpServiceAddressAllocated(srvResource.Metadata.Name);
                    }

                    if (needAddressAllocated.Count == 0)
                    {
                        return; // We are done
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            // If there are still services that need address allocated, try a final direct query in case the watch missed some updates.
            foreach (var sar in needAddressAllocated)
            {
                var dcpSvc = await _kubernetesService.GetAsync<Service>(sar.Metadata.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (dcpSvc.HasCompleteAddress)
                {
                    sar.ApplyAddressInfoFrom(dcpSvc);
                }
                else
                {
                    _distributedApplicationLogger.LogWarning("Unable to allocate a network port for service '{ServiceName}'; service may be unreachable and its clients may not work properly.", sar.Metadata.Name);
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
            var containerServices = _appResources.Where(r => r.DcpResource is Service { }).Select(r => (
                Service: r.DcpResource as Service,
                PrimaryServiceName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.PrimaryServiceNameAnnotation, out var psn) == true ? psn : null)
            )
            .Where(cs => !string.IsNullOrEmpty(cs.PrimaryServiceName) && cs.Service?.HasCompleteAddress is not true);

            foreach (var cs in containerServices)
            {
                var primaryService = _appResources.OfType<ServiceWithModelResource>().Select(sar => sar.Service)
                    .First(svc => svc.Metadata.Name.Equals(cs.PrimaryServiceName));
                cs.Service!.ApplyAddressInfoFrom(primaryService);
                cs.Service!.Status!.EffectiveAddress = ContainerHostName;
            }
        }
        finally
        {
            AspireEventSource.Instance.DcpServiceAddressAllocationStop(initialServiceCount - needAddressAllocated.Count);
        }
    }

    private Task CreateAllDcpObjectsAsync<RT>(CancellationToken cancellationToken) where RT : CustomResource, IKubernetesStaticMetadata
    {
        var objects = _appResources.Select(r => r.DcpResource).OfType<RT>();
        return CreateDcpObjectsAsync(objects, cancellationToken);
    }

    private async Task CreateDcpObjectsAsync<RT>(IEnumerable<RT> objects, CancellationToken cancellationToken) where RT : CustomResource, IKubernetesStaticMetadata
    {
        var toCreate = objects.ToImmutableArray();
        if (toCreate.Length == 0)
        {
            return;
        }

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
                        AspireEventSource.Instance.DcpObjectCreationStart(rtc.Kind, rtc.Metadata.Name);
                        await _kubernetesService.CreateAsync(rtc, cancellationToken).ConfigureAwait(false);
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
            // We catch and suppress the OperationCancelledException because the user may CTRL-C
            // during start up of the resources.
            _logger.LogDebug(ex, "Cancellation during creation of resources.");
        }
        finally
        {
            AspireEventSource.Instance.DcpObjectSetCreationStop(RT.ObjectKind, toCreate.Length);
        }
    }

    // Specifies which endpoints to process when creating AllocatedEndpoint info
    [Flags]
    private enum AllocatedEndpointsMode
    {
        Workload = 0x1, // Process endpoints produced by workload resources (Executables and Containers)
        ContainerTunnel = 0x2, // Process endpoints produced by container tunnels
        All = 0xFF // Process endpoints produced by all resources, including container tunnels
    }

    // Adds allocated endpoints for all relevant resources in the model
    private void AddAllocatedEndpointInfo(IEnumerable<RenderedModelResource> resources, AllocatedEndpointsMode mode = AllocatedEndpointsMode.Workload)
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

                    var (targetHost, bindingMode) = NormalizeTargetHost(sp.EndpointAnnotation.TargetHost);

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

                var tunnelServices = _appResources.Select(r => (
                    Service: r.DcpResource as Service,
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

                    var serverSvc = _appResources.OfType<ServiceWithModelResource>().FirstOrDefault(swr =>
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
                        (int)port,
                        EndpointBindingMode.SingleAddress,
                        targetPortExpression: $$$"""{{- portForServing "{{{ts.Service.Name}}}" -}}""",
                        networkID
                    );
                    endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(networkID, tunnelAllocatedEndpoint);
                }
            }
        }

    }

    private void PrepareContainerNetworks()
    {
        var containerResources = _model.Resources.Where(mr => mr.IsContainer());
        if (!containerResources.Any()) { return; }

        var network = ContainerNetwork.Create(KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);
        if (containerResources.Any(cr => cr.GetContainerLifetimeType() == ContainerLifetime.Persistent))
        {
            // If we have any persistent container resources
            network.Spec.Persistent = true;
            // Persistent networks require a predictable name to be reused between runs.
            // Append the same project hash suffix used for persistent container names.
            network.Spec.NetworkName = $"{DefaultAspirePersistentNetworkName}-{_nameGenerator.GetProjectHashSuffix()}";
        }
        else
        {
            network.Spec.NetworkName = $"{DefaultAspireNetworkName}-{DcpNameGenerator.GetRandomNameSuffix()}";
        }

        if (!string.IsNullOrEmpty(_normalizedApplicationName))
        {
            var shortApplicationName = _normalizedApplicationName.Length < 32 ? _normalizedApplicationName : _normalizedApplicationName.Substring(0, 32);
            network.Spec.NetworkName += $"-{shortApplicationName}"; // Limit to 32 characters to avoid exceeding resource name length limits.
        }

        _appResources.Add(new AppResource(network));
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
        var hostResources = _model.Resources.Select(AsHostResourceWithEndpoints).OfType<HostResourceWithEndpoints>().ToList();

        foreach (var re in hostResources)
        {
            var containerNetworkServices = CreateContainerNetworkServicesForHostResource(re);
            _appResources.AddRange(containerNetworkServices.Select(cns => cns.ServiceResource));
        }
    }

    private IEnumerable<ContainerNetworkService> CreateContainerNetworkServicesForHostResource(HostResourceWithEndpoints re)
    {
        var resourceLogger = _loggerService.GetLogger(re.Resource);
        var services = new List<ContainerNetworkService>();
        var useTunnel = _options.Value.EnableAspireContainerTunnel;
        string tunnelProxyName = useTunnel ? GetTunnelProxyResourceName() : "";

        foreach (var endpoint in re.Endpoints)
        {
            var (serviceName, isNew) = _nameGenerator.GetServiceName(re.Resource, endpoint, KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
            if (!isNew)
            {
                // Entirely possible that multiple container resources reference the same host resource and endpoint.
                // We let the first container creation task (exists == false) create the service and other tasks just leverages it.
                continue;
            }

            if (useTunnel && endpoint.Protocol != ProtocolType.Tcp)
            {
                resourceLogger.LogWarning("Host endpoint '{EndpointName}' on resource '{HostResource}' is referenced by a container resource, but the endpoint is using a network protocol '{Protocol}' other than TCP. Only TCP is supported for container-to-host references.", endpoint.Name, re.Resource.Name, endpoint.Protocol);
                continue;
            }

            var svc = Service.Create(serviceName);
            svc.Spec.AddressAllocationMode = AddressAllocationModes.Proxyless;
            svc.Spec.Protocol = PortProtocol.FromProtocolType(endpoint.Protocol);
            // Address and port will be set automatically by DCP.

            var serverSvc = _appResources.OfType<ServiceWithModelResource>().FirstOrDefault(swr =>
                StringComparers.ResourceName.Equals(swr.ModelResource.Name, re.Resource.Name) &&
                StringComparers.EndpointAnnotationName.Equals(swr.EndpointAnnotation.Name, endpoint.Name)
            );
            if (serverSvc is null)
            {
                // This should never happen--if a host resource has an Endpoint, we should have created a Service for it.
                throw new InvalidDataException($"Host endpoint '{endpoint.Name}' on resource '{re.Resource.Name}' should have an associated DCP Service resource already set up");
            }

            TunnelConfiguration? tunnelConfig = null;
            if (useTunnel)
            {
                tunnelConfig = new TunnelConfiguration
                {
                    Name = serviceName,
                    ServerServiceName = serverSvc.DcpResource.Metadata.Name,
                    ServerServiceNamespace = string.Empty,
                    ClientServiceName = svc.Metadata.Name,
                    ClientServiceNamespace = string.Empty
                };
            }

            svc.Annotate(CustomResource.ResourceNameAnnotation, re.Resource.Name);  // Resource that implements the service behind the Endpoint.
            svc.Annotate(CustomResource.EndpointNameAnnotation, endpoint.Name);
            svc.Annotate(CustomResource.ContainerNetworkAnnotation, KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);
            svc.Annotate(CustomResource.PrimaryServiceNameAnnotation, serverSvc.DcpResource.Metadata.Name);

            // We use this to distinguish services based on real tunnel proxies
            // vs "placeholders" relying on host.docker.internal bridge (when tunnel is disabled).
            svc.Annotate(CustomResource.ContainerTunnelInstanceName, tunnelProxyName);

            var svcAppResource = new ServiceAppResource(svc);
            services.Add(new ContainerNetworkService { ServiceResource = svcAppResource, TunnelConfig = tunnelConfig });
        }

        return services;
    }

    private void PrepareExecutables()
    {
        PrepareProjectExecutables();
        PreparePlainExecutables();
        PrepareContainerExecutables();
    }

    private void PrepareContainerExecutables()
    {
        var modelContainerExecutableResources = _model.GetContainerExecutableResources();
        foreach (var containerExecutable in modelContainerExecutableResources)
        {
            EnsureRequiredAnnotations(containerExecutable);
            var exeInstance = GetDcpInstance(containerExecutable, instanceIndex: 0);

            // Container exec runs against a dcp container resource, so its required to resolve a DCP name of the resource
            // since this is ContainerExec resource, we will run against one of the container instances
            var containerDcpName = containerExecutable.TargetContainerResource!.GetResolvedResourceName();

            var containerExec = ContainerExec.Create(
                name: exeInstance.Name,
                containerName: containerDcpName,
                command: containerExecutable.Command,
                args: containerExecutable.Args?.ToList(),
                workingDirectory: containerExecutable.WorkingDirectory);

            containerExec.Annotate(CustomResource.OtelServiceNameAnnotation, containerExecutable.Name);
            containerExec.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, exeInstance.Suffix);
            containerExec.Annotate(CustomResource.ResourceNameAnnotation, containerExecutable.Name);
            SetInitialResourceState(containerExecutable, containerExec);

            var exeAppResource = new RenderedModelResource(containerExecutable, containerExec);
            _appResources.Add(exeAppResource);
        }
    }

    private void PreparePlainExecutables()
    {
        var modelExecutableResources = _model.GetExecutableResources();
        var executablesList = modelExecutableResources.ToList(); // Materialize to check count

        foreach (var executable in executablesList)
        {
            EnsureRequiredAnnotations(executable);

            var exeInstance = GetDcpInstance(executable, instanceIndex: 0);
            var exePath = executable.Command;
            var exe = Executable.Create(exeInstance.Name, exePath);

            // The working directory is always relative to the app host project directory (if it exists).
            exe.Spec.WorkingDirectory = executable.WorkingDirectory;
            exe.Annotate(CustomResource.OtelServiceNameAnnotation, executable.Name);
            exe.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, exeInstance.Suffix);
            exe.Annotate(CustomResource.ResourceNameAnnotation, executable.Name);

            if (executable.SupportsDebugging(_configuration, out _))
            {
                // Just mark as IDE execution here - the actual launch configuration callback
                // will be invoked in CreateExecutableAsync after endpoints are allocated.
                exe.Spec.ExecutionType = ExecutionType.IDE;
                exe.Spec.FallbackExecutionTypes = [ ExecutionType.Process ];
            }
            else
            {
                exe.Spec.ExecutionType = ExecutionType.Process;
            }

            SetInitialResourceState(executable, exe);

            var exeAppResource = new RenderedModelResource(executable, exe);
            AddServicesProducedInfo(executable, exe, exeAppResource);
            _appResources.Add(exeAppResource);
        }
    }

    private void PrepareProjectExecutables()
    {
        var modelProjectResources = _model.GetProjectResources();

        foreach (var project in modelProjectResources)
        {
            if (!project.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
            {
                throw new InvalidOperationException("A project resource is missing required metadata"); // Should never happen.
            }

            EnsureRequiredAnnotations(project);

            var replicas = project.GetReplicaCount();

            for (var i = 0; i < replicas; i++)
            {
                var exeInstance = GetDcpInstance(project, instanceIndex: i);
                var exe = Executable.Create(exeInstance.Name, "dotnet");
                exe.Spec.WorkingDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath);

                exe.Annotate(CustomResource.OtelServiceNameAnnotation, project.Name);
                exe.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, exeInstance.Suffix);
                exe.Annotate(CustomResource.ResourceNameAnnotation, project.Name);
                exe.Annotate(CustomResource.ResourceReplicaCount, replicas.ToString(CultureInfo.InvariantCulture));
                exe.Annotate(CustomResource.ResourceReplicaIndex, i.ToString(CultureInfo.InvariantCulture));

                SetInitialResourceState(project, exe);

                var projectArgs = new List<string>();

                var isInDebugSession = !string.IsNullOrEmpty(_configuration[DebugSessionPortVar]);

                if (project.SupportsDebugging(_configuration, out var supportsDebuggingAnnotation))
                {
                    exe.Spec.ExecutionType = ExecutionType.IDE;
                    exe.Spec.FallbackExecutionTypes = [ ExecutionType.Process ];

                    if (supportsDebuggingAnnotation.LaunchConfigurationType is "project")
                    {
                        // We want this annotation even if we are not using IDE execution; see ToSnapshot() for details.
                        exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, CreateProjectLaunchConfiguration(project, projectMetadata));
                    }
                    // Non-project launch types (e.g. azure-functions) have their launch configuration
                    // applied later in CreateExecutableAsync() after endpoints are allocated,
                    // unless the IDE didn't send DEBUG_SESSION_INFO (handled by the fallback branch below).
                }
                else if (ShouldFallBackToIdeExecution(isInDebugSession, supportsDebuggingAnnotation))
                {
                    // Fall back to IDE execution with a standard ProjectLaunchConfiguration when:
                    // 1. No SupportsDebuggingAnnotation exists (e.g. AddResource-based ProjectResource
                    //    subclasses that don't call WithDebugSupport). These should get the same IDE
                    //    treatment that AddProject provides by default.
                    // 2. The annotation exists but the IDE did not send DEBUG_SESSION_INFO (Visual Studio
                    //    scenario). VS handles all project resources natively, so non-"project" types
                    //    like "azure-functions" still need IDE execution with ProjectLaunchConfiguration.
                    exe.Spec.ExecutionType = ExecutionType.IDE;
                    exe.Spec.FallbackExecutionTypes = [ExecutionType.Process];

                    exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, CreateProjectLaunchConfiguration(project, projectMetadata));
                }
                else
                {
                    exe.Spec.ExecutionType = ExecutionType.Process;

                    var projectLaunchConfiguration = new ProjectLaunchConfiguration();
                    projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;

                    // `dotnet watch` does not work with file-based apps yet, so we have to use `dotnet run` in that case
                    if (_configuration.GetBool("DOTNET_WATCH") is not true || projectMetadata.IsFileBasedApp)
                    {
                        projectArgs.Add("run");
                        projectArgs.Add(projectMetadata.IsFileBasedApp ? "--file" : "--project");
                        projectArgs.Add(projectMetadata.ProjectPath);
                        if (projectMetadata.IsFileBasedApp)
                        {
                            projectArgs.Add("--no-cache");
                        }
                        if (projectMetadata.SuppressBuild)
                        {
                            projectArgs.Add("--no-build");
                        }
                    }
                    else
                    {
                        projectArgs.AddRange([
                            "watch",
                            "--non-interactive",
                            "--no-hot-reload",
                            "--project",
                            projectMetadata.ProjectPath
                        ]);
                    }

                    if (!string.IsNullOrEmpty(_distributedApplicationOptions.Configuration))
                    {
                        projectArgs.AddRange(new[] { "--configuration", _distributedApplicationOptions.Configuration });
                    }

                    // We pretty much always want to suppress the normal launch profile handling
                    // because the settings from the profile will override the ambient environment settings, which is not what we want
                    // (the ambient environment settings for service processes come from the application model
                    // and should be HIGHER priority than the launch profile settings).
                    // This means we need to apply the launch profile settings manually inside CreateExecutableAsync().
                    projectArgs.Add("--no-launch-profile");

                    // We want this annotation even if we are not using IDE execution; see ToSnapshot() for details.
                    exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, projectLaunchConfiguration);
                }

                exe.SetAnnotationAsObjectList(CustomResource.ResourceProjectArgsAnnotation, projectArgs);

                var exeAppResource = new RenderedModelResource(project, exe);
                AddServicesProducedInfo(project, exe, exeAppResource);
                _appResources.Add(exeAppResource);
            }
        }
    }

    /// <summary>
    /// Determines whether to fall back to IDE execution for a project resource that did not
    /// pass <see cref="ExtensionUtils.SupportsDebugging"/>. Returns <see langword="true"/> when
    /// the app host is running inside a debug session and either the resource has no
    /// <see cref="SupportsDebuggingAnnotation"/> (e.g. AddResource-based subclasses) or the
    /// IDE did not send <c>DEBUG_SESSION_INFO</c> (Visual Studio scenario).
    /// </summary>
    private bool ShouldFallBackToIdeExecution(bool isInDebugSession, SupportsDebuggingAnnotation? supportsDebuggingAnnotation)
    {
        if (!isInDebugSession)
        {
            return false;
        }

        if (supportsDebuggingAnnotation is not null && !string.IsNullOrEmpty(_configuration[KnownConfigNames.DebugSessionInfo]))
        {
            return false;
        }

        return true;
    }

    private ProjectLaunchConfiguration CreateProjectLaunchConfiguration(ProjectResource project, IProjectMetadata projectMetadata)
    {
        var projectLaunchConfiguration = new ProjectLaunchConfiguration();
        projectLaunchConfiguration.ProjectPath = projectMetadata.ProjectPath;
        projectLaunchConfiguration.Mode = _configuration[KnownConfigNames.DebugSessionRunMode]
            ?? (Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug);

        projectLaunchConfiguration.DisableLaunchProfile = project.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _);
        // Use the effective launch profile which has fallback logic
        if (!projectLaunchConfiguration.DisableLaunchProfile && project.GetEffectiveLaunchProfile() is NamedLaunchProfile namedLaunchProfile)
        {
            projectLaunchConfiguration.LaunchProfile = namedLaunchProfile.Name;
        }

        return projectLaunchConfiguration;
    }

    private void EnsureRequiredAnnotations(IResource resource)
    {
        // Add the default lifecycle commands (start/stop/restart)
        resource.AddLifeCycleCommands();

        _nameGenerator.EnsureDcpInstancesPopulated(resource);
    }

    private static void SetInitialResourceState(IResource resource, IAnnotationHolder annotationHolder)
    {
        // Store the initial state of the resource
        if (resource.TryGetLastAnnotation<ResourceSnapshotAnnotation>(out var initial) &&
            initial.InitialSnapshot.State?.Text is string state && !string.IsNullOrEmpty(state))
        {
            annotationHolder.Annotate(CustomResource.ResourceStateAnnotation, state);
        }
    }

    private Task CreateContainerExecutablesAsync(IEnumerable<RenderedModelResource> containerExecAppResources, CancellationToken cancellationToken)
        => CreateRenderedResourcesAsync(CreateContainerExecutableAsync, containerExecAppResources, Model.Dcp.ContainerExecKind, cancellationToken);

    private Task CreateExecutablesAsync(IEnumerable<RenderedModelResource> execAppResources, CancellationToken cancellationToken)
        => CreateRenderedResourcesAsync(CreateExecutableAsync, execAppResources, Model.Dcp.ExecutableKind, cancellationToken);

    private async Task CreateRenderedResourcesAsync(
       Func<RenderedModelResource, ILogger, CancellationToken, Task> createResourceFunc,
       IEnumerable<RenderedModelResource> executables,
       string executableKind,
       CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        var groups = executables.GroupBy(e => e.ModelResource).ToList();
        var executableCount = executables.Count();

        try
        {
            AspireEventSource.Instance.DcpObjectSetCreationStart(executableKind, executableCount);
            foreach (var group in groups)
            {
                var groupList = group.ToList();
                var groupKey = group.Key;
                // Materialize the group with ToList() to avoid issues with deferred execution of IGrouping.
                // Force this to be async so that blocking code does not stop other executables from being created.
                tasks.Add(Task.Run(() => CreateResourceExecutablesAsyncCore(groupKey, groupList, createResourceFunc, cancellationToken), cancellationToken));
            }

            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AspireEventSource.Instance.DcpObjectSetCreationStop(executableKind, executableCount);
        }
    }

    async Task CreateResourceExecutablesAsyncCore(
        IResource resource,
        IEnumerable<RenderedModelResource> executables,
         Func<RenderedModelResource, ILogger, CancellationToken, Task> createResourceFunc,
        CancellationToken cancellationToken)
    {
        var resourceLogger = _loggerService.GetLogger(resource);
        var resourceType = resource is ProjectResource ? KnownResourceTypes.Project : KnownResourceTypes.Executable;

        try
        {
            AspireEventSource.Instance.CreateAspireExecutableResourcesStart(resource.Name);

            var explicitStartup = resource.TryGetAnnotationsOfType<ExplicitStartupAnnotation>(out _) is true;

            // Publish snapshots built from DCP resources. Do this now to populate more values from DCP (source) to ensure they're
            // available if the resource isn't immediately started because it's waiting or is configured for explicit start.
            foreach (var er in executables)
            {
                Func<CustomResourceSnapshot, CustomResourceSnapshot> snapshotBuild = er.DcpResource switch
                {
                    Executable exe => s => _snapshotBuilder.ToSnapshot(exe, s),
                    ContainerExec exe => s => _snapshotBuilder.ToSnapshot(exe, s),
                    _ => throw new NotImplementedException($"Does not support snapshots for resources of type like '{er.DcpResourceName}' is ")
                };

                await _executorEvents.PublishAsync(new OnResourceChangedContext(
                    _shutdownCancellation.Token, resourceType, resource,
                    er.DcpResourceName, new ResourceStatus(null, null, null),
                    snapshotBuild)
                ).ConfigureAwait(false);

                if (explicitStartup)
                {
                    // If explicit startup is configured, we need to set the resource state to NotStarted
                    await _executorEvents.PublishAsync(new OnResourceChangedContext(
                        cancellationToken, resourceType, resource,
                        er.DcpResource.Metadata.Name, new ResourceStatus(KnownResourceStates.NotStarted, null, null), s => s with { State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, null) })
                    ).ConfigureAwait(false);
                }
            }

            if (explicitStartup)
            {
                // If explicit startup is configured, we aren't going to start the resource now.
                // We need to exit before we send the BeforeResourceStarted event or create any DCP resources.
                // The resource can be explicitly started later via user action or API at which point BeforeResourceStarted
                // will be published and the DCP resource created.
                return;
            }

            await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, resource)).ConfigureAwait(false);
            await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, resource, DcpResourceName: null)).ConfigureAwait(false);
            foreach (var er in executables)
            {
                try
                {
                    await createResourceFunc(er, resourceLogger, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Expected cancellation during shutdown - propagate clean cancellation
                    throw;
                }
                catch (FailedToApplyEnvironmentException)
                {
                    // For this exception we don't want the noise of the stack trace, we've already
                    // provided more detail where we detected the issue (e.g. envvar name). To get
                    // more diagnostic information reduce logging level for DCP log category to Debug.
                    await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, er.ModelResource, er.DcpResource.Metadata.Name)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The purpose of this catch block is to ensure that if an individual executable resource fails
                    // to start that it doesn't tear down the entire app host AND that we route the error to the
                    // appropriate replica.
                    resourceLogger.LogError(ex, "Failed to create resource {ResourceName}", er.ModelResource.Name);
                    await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, er.ModelResource, er.DcpResource.Metadata.Name)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // The purpose of this catch block is to ensure that if an error processing the overall
            // configuration of the executable resource files. This is different to the exception handling
            // block above because at this stage of processing we don't necessarily have any replicas
            // yet. For example if a dependency fails to start.
            resourceLogger.LogError(ex, "Failed to create resource {ResourceName}", resource.Name);
            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, resource, DcpResourceName: null)).ConfigureAwait(false);
        }
        finally
        {
            AspireEventSource.Instance.CreateAspireExecutableResourcesStop(resource.Name);
        }
    }

    private async Task CreateContainerExecutableAsync(RenderedModelResource er, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        if (er.DcpResource is not ContainerExec containerExe)
        {
            throw new InvalidOperationException($"Expected an {nameof(ContainerExec)} resource, but got {er.DcpResource.Kind} instead");
        }
        var spec = containerExe.Spec;

        try
        {
            AspireEventSource.Instance.DcpObjectCreationStart(er.DcpResource.Kind, er.DcpResourceName);
            await _kubernetesService.CreateAsync(containerExe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AspireEventSource.Instance.DcpObjectCreationStop(er.DcpResource.Kind, er.DcpResourceName);
        }
    }

    private async Task CreateExecutableAsync(RenderedModelResource er, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        if (er.DcpResource is not Executable exe)
        {
            throw new InvalidOperationException($"Expected an Executable resource, but got {er.DcpResource.Kind} instead");
        }
        try
        {
            AspireEventSource.Instance.DcpObjectCreationStart(er.DcpResource.Kind, er.DcpResourceName);
            cancellationToken.ThrowIfCancellationRequested();

            var spec = exe.Spec;

            // Don't create an args collection unless needed. A null args collection means a project run by the will use args provided by the launch profile.
            // https://github.com/microsoft/aspire/blob/main/docs/specs/IDE-execution.md#launch-profile-processing-project-launch-configuration
            spec.Args = null;

            // An executable can be restarted so args must be reset to an empty state.
            // After resetting, first apply any dotnet project related args, e.g. configuration, and then add args from the model resource.
            if (er.DcpResource.TryGetAnnotationAsObjectList<string>(CustomResource.ResourceProjectArgsAnnotation, out var projectArgs) && projectArgs.Count > 0)
            {
                spec.Args ??= [];
                spec.Args.AddRange(projectArgs);
            }

            // Build the base paths for certificate output in the DCP session directory.
            var certificatesRootDir = Path.Join(_locations.DcpSessionDir, exe.Name());
            var bundleOutputPath = Path.Join(certificatesRootDir, "cert.pem");
            var customBundleOutputPath = Path.Join(certificatesRootDir, "bundles");
            var certificatesOutputPath = Path.Join(certificatesRootDir, "certs");
            var baseServerAuthOutputPath = Path.Join(certificatesRootDir, "private");

            var configuration = await ExecutionConfigurationBuilder.Create(er.ModelResource)
                .WithArgumentsConfig()
                .WithEnvironmentVariablesConfig()
                .WithCertificateTrustConfig(scope =>
                {
                    var dirs = new List<string> { certificatesOutputPath };
                    if (scope == CertificateTrustScope.Append)
                    {
                        var existing = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
                        if (!string.IsNullOrEmpty(existing))
                        {
                            dirs.AddRange(existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
                        }
                    }

                    return new()
                    {
                        CertificateBundlePath = ReferenceExpression.Create($"{bundleOutputPath}"),
                        // Build the SSL_CERT_DIR value by combining the new certs directory with any existing directories.
                        CertificateDirectoriesPath = ReferenceExpression.Create($"{string.Join(Path.PathSeparator, dirs)}"),
                        RootCertificatesPath = certificatesRootDir,
                    };
                })
                .WithHttpsCertificateConfig(cert => new()
                {
                    CertificatePath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.crt")}"),
                    KeyPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.key")}"),
                    PfxPath = ReferenceExpression.Create($"{Path.Join(baseServerAuthOutputPath, $"{cert.Thumbprint}.pfx")}"),
                })
                .BuildAsync(_executionContext, resourceLogger, cancellationToken)
                .ConfigureAwait(false);

            // Add the certificates to the executable spec so they'll be placed in the DCP config
            ExecutablePemCertificates? pemCertificates = null;
            if (configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var certificateTrustConfiguration)
                && certificateTrustConfiguration.Scope != CertificateTrustScope.None
                && certificateTrustConfiguration.Certificates.Count > 0)
            {
                pemCertificates = new ExecutablePemCertificates
                {
                    Certificates = certificateTrustConfiguration.Certificates.Select(c =>
                    {
                        return new PemCertificate
                        {
                            Thumbprint = c.Thumbprint,
                            Contents = c.ExportCertificatePem(),
                        };
                    }).DistinctBy(cert => cert.Thumbprint).ToList(),
                    ContinueOnError = true,
                };

                if (certificateTrustConfiguration.CustomBundlesFactories.Count > 0)
                {
                    Directory.CreateDirectory(customBundleOutputPath);
                }

                foreach (var bundleFactory in certificateTrustConfiguration.CustomBundlesFactories)
                {
                    var bundleId = bundleFactory.Key;
                    var bundleBytes = await bundleFactory.Value(certificateTrustConfiguration.Certificates, cancellationToken).ConfigureAwait(false);

                    File.WriteAllBytes(Path.Join(customBundleOutputPath, bundleId), bundleBytes);
                }
            }

            exe.Spec.PemCertificates = pemCertificates;

            if (configuration.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var tlsCertificateConfiguration))
            {
                var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
                var publicCetificatePem = tlsCertificateConfiguration.Certificate.ExportCertificatePem();
                (var keyPem, var pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(tlsCertificateConfiguration.Certificate, tlsCertificateConfiguration.Password, tlsCertificateConfiguration.IsKeyPathReferenced, tlsCertificateConfiguration.IsPfxPathReferenced, cancellationToken).ConfigureAwait(false);

                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(baseServerAuthOutputPath);
                }
                else
                {
                    Directory.CreateDirectory(baseServerAuthOutputPath, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);
                }

                File.WriteAllText(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.crt"), publicCetificatePem);

                if (keyPem is not null)
                {
                    var keyBytes = Encoding.ASCII.GetBytes(keyPem);

                    // Write each of the certificate, key, and PFX assets to the temp folder
                    File.WriteAllBytes(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.key"), keyBytes);

                    Array.Clear(keyPem, 0, keyPem.Length);
                    Array.Clear(keyBytes, 0, keyBytes.Length);
                }

                if (pfxBytes is not null)
                {
                    File.WriteAllBytes(Path.Join(baseServerAuthOutputPath, $"{thumbprint}.pfx"), pfxBytes);
                    Array.Clear(pfxBytes, 0, pfxBytes.Length);
                }
            }

            var launchArgs = BuildLaunchArgs(er, spec, configuration.Arguments);
            var executableArgs = launchArgs.Where(a => a.Executable).Select(a => a.Value).ToList();
            var displayArgs = launchArgs.Where(a => a.Display).ToList();
            if (executableArgs.Count > 0)
            {
                spec.Args ??= [];
                spec.Args.AddRange(executableArgs);
            }
            // Arg annotations are what is displayed in the dashboard.
            er.DcpResource.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, displayArgs.Select(a => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive)));

            spec.Env = configuration.EnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList();

            if (configuration.Exception is not null)
            {
                throw new FailedToApplyEnvironmentException();
            }

            // Invoke the debug configuration callback now that endpoints are allocated.
            // This allows launch configurations to access endpoint URLs that were not
            // available during PrepareExecutables().
            // "project" launch types configure their launch configs in PrepareProjectExecutables() directly;
            // all other types (plain executables and project subtypes like azure-functions) are handled here.
            if (er.ModelResource.SupportsDebugging(_configuration, out var supportsDebuggingAnnotation)
                && supportsDebuggingAnnotation.LaunchConfigurationType is not "project")
            {
                var mode = _configuration[KnownConfigNames.DebugSessionRunMode] ?? ExecutableLaunchMode.NoDebug;
                try
                {
                    // Clear any existing launch configurations (needed for restart scenarios).
                    exe.Annotate(Executable.LaunchConfigurationsAnnotation, string.Empty);
                    supportsDebuggingAnnotation.LaunchConfigurationAnnotator(exe, mode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply launch configuration for resource '{ResourceName}'. Falling back to process execution.", er.ModelResource.Name);
                    exe.Spec.ExecutionType = ExecutionType.Process;
                }
            }

            await _kubernetesService.CreateAsync(exe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AspireEventSource.Instance.DcpObjectCreationStop(er.DcpResource.Kind, er.DcpResourceName);
        }
    }

    private static List<(string Value, bool IsSensitive, bool Executable, bool Display)> BuildLaunchArgs(RenderedModelResource er, ExecutableSpec spec, IEnumerable<(string Value, bool IsSensitive)> appHostArgs)
    {
        // Launch args is the final list of args that are displayed in the UI and possibly added to the executable spec.
        // They're built from app host resource model args and any args in the effective launch profile.
        // Follows behavior in the IDE execution spec when in IDE execution mode:
        // https://github.com/microsoft/aspire/blob/main/docs/specs/IDE-execution.md#project-launch-configuration-type-project
        var launchArgs = new List<(string Value, bool IsSensitive, bool Executable, bool Display)>();

        // If the executable is a project then include any command line args from the launch profile.
        if (er.ModelResource is ProjectResource project)
        {
            // Args in the launch profile is used when:
            // 1. The project is run as an executable. Launch profile args are combined with app host supplied args.
            // 2. The project is run by the IDE and no app host args are specified.
            if (spec.ExecutionType == ExecutionType.Process || (spec.ExecutionType == ExecutionType.IDE && !appHostArgs.Any()))
            {
                // When the .NET project is launched from an IDE the launch profile args are automatically added.
                // We still want to display the args in the dashboard so only add them to the custom arg annotations.
                var executableArg = spec.ExecutionType != ExecutionType.IDE;

                var launchProfileArgs = GetLaunchProfileArgs(project.GetEffectiveLaunchProfile()?.LaunchProfile);
                if (launchProfileArgs.Count > 0 && appHostArgs.Any())
                {
                    // If there are app host args, add a double-dash to separate them from the launch args.
                    launchProfileArgs.Insert(0, "--");
                }

                launchArgs.AddRange(launchProfileArgs.Select(a => (a, isSensitive: false, executableArg, true)));
            }
        }
#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        else if (er.ModelResource is DotnetToolResource tool)
        {
            var argSeparator = appHostArgs.Select((a, i) => (index: i, value: a.Value))
                .FirstOrDefault(x => x.value == DotnetToolResourceExtensions.ArgumentSeparator);

            var args = appHostArgs.Select((a, i) => (arg: a, display: i > argSeparator.index));
            launchArgs.AddRange(args.Select(x => (x.arg.Value, x.arg.IsSensitive, true, x.display)));
            return launchArgs;

        }
#pragma warning restore ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        // In the situation where args are combined (process execution) the app host args are added after the launch profile args.
        launchArgs.AddRange(appHostArgs.Select(a => (a.Value, a.IsSensitive, true, true)));

        return launchArgs;
    }

    private static List<string> GetLaunchProfileArgs(LaunchProfile? launchProfile)
    {
        if (launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs))
        {
            return CommandLineArgsParser.Parse(launchProfile.CommandLineArgs);
        }

        return [];
    }

    private void PrepareContainers()
    {
        var modelContainerResources = _model.GetContainerResources();

        foreach (var container in modelContainerResources)
        {
            if (!container.TryGetContainerImageName(out var containerImageName))
            {
                // This should never happen! In order to get into this loop we need
                // to have the annotation, if we don't have the annotation by the time
                // we get here someone is doing something wrong.
                throw new InvalidOperationException();
            }

            EnsureRequiredAnnotations(container);

            var containerObjectInstance = GetDcpInstance(container, instanceIndex: 0);
            var ctr = Container.Create(containerObjectInstance.Name, containerImageName);

            ctr.Spec.ContainerName = containerObjectInstance.Name; // Use the same name for container orchestrator (Docker, Podman) resource and DCP object name.

            if (container.GetContainerLifetimeType() == ContainerLifetime.Persistent)
            {
                ctr.Spec.Persistent = true;
            }

            if (container.TryGetContainerImagePullPolicy(out var pullPolicy))
            {
                ctr.Spec.PullPolicy = pullPolicy switch
                {
                    ImagePullPolicy.Default => null,
                    ImagePullPolicy.Always => ContainerPullPolicy.Always,
                    ImagePullPolicy.Missing => ContainerPullPolicy.Missing,
                    ImagePullPolicy.Never => ContainerPullPolicy.Never,
                    _ => throw new InvalidOperationException($"Unknown pull policy '{Enum.GetName(typeof(ImagePullPolicy), pullPolicy)}' for container '{container.Name}'")
                };
            }

            ctr.Annotate(CustomResource.ResourceNameAnnotation, container.Name);
            ctr.Annotate(CustomResource.OtelServiceNameAnnotation, container.Name);
            ctr.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, containerObjectInstance.Suffix);
            SetInitialResourceState(container, ctr);

            var aanns = container.Annotations.OfType<ContainerNetworkAliasAnnotation>().ToImmutableArray();
            if (aanns.Any(a => a.Network != KnownNetworkIdentifiers.DefaultAspireContainerNetwork))
            {
                throw new InvalidOperationException("Custom container networks are not supported yet.");
            }

            ctr.Spec.Networks = new List<ContainerNetworkConnection>
            {
                new ContainerNetworkConnection
                {
                    Name = KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value,
                    Aliases = aanns.Select(a => a.Alias)
                                .Prepend($"{container.Name}.dev.internal") // Alias to .dev.internal to support dev cert trust
                                .Prepend(container.Name)
                                .ToList()
                }
            };

            var containerAppResource = new RenderedModelResource(container, ctr);
            AddServicesProducedInfo(container, ctr, containerAppResource);
            _appResources.Add(containerAppResource);
        }
    }

    /// <summary>
    /// Gets information about the resource's DCP instance. ReplicaInstancesAnnotation is added in BeforeStartEvent.
    /// </summary>
    private static DcpInstance GetDcpInstance(IResource resource, int instanceIndex)
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

    private async Task CreateSingleContainerAsync(RenderedModelResource cr, ContainerCreationContext cctx, CancellationToken cToken)
    {
        var dcpContainer = (Container)cr.DcpResource;
        AspireEventSource.Instance.DcpObjectCreationStart(dcpContainer.Kind, dcpContainer.Metadata.Name);
        var signalServicesSpecReadyOnce = ConcurrencyUtils.Once(() => cctx.ContainerServicesSpecReady.Signal());

        try
        {
            cToken.ThrowIfCancellationRequested();
            var logger = _loggerService.GetLogger(cr.ModelResource);
            AddAllocatedEndpointInfo([cr], AllocatedEndpointsMode.Workload);

            try
            {
                // In previous versions of Aspire we would delay raising BeforeResourceStarted event for explicit startup resources.
                // But we need to determine whether the container is using any host resources,
                // and need to call environment and argument annotation callbacks in the process, and this means
                // we need to raise this event now, so that "last chance dynamic setup or validation" can be performed for the resource
                // (see docs/specs/appmodel.md#well-known-lifecycle-events)
                await _executorEvents.PublishAsync(new OnResourceStartingContext(cToken, KnownResourceTypes.Container, cr.ModelResource, dcpContainer.Metadata.Name)).ConfigureAwait(false);

                // Publish snapshot built from DCP resource. Do this now to populate more values from DCP (source) to ensure they're
                // available if the resource isn't immediately started because it's waiting or is configured for explicit start.
                await _executorEvents.PublishAsync(new OnResourceChangedContext(_shutdownCancellation.Token, KnownResourceTypes.Container, cr.ModelResource, cr.DcpResourceName, new ResourceStatus(null, null, null), s => _snapshotBuilder.ToSnapshot((Container)cr.DcpResource, s))).ConfigureAwait(false);

                // Note: resource restart is done by calling StartResourceAsync(), which sets Spec.Start to true and
                // calls CreateDcpContainerAsync() directly, bypassing the following check.
                var explicitStartup = cr.ModelResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _);
                if (explicitStartup)
                {
                    dcpContainer.Spec.Start = false;
                }

                var hostDependencies = (await GetHostDependenciesAsync(cr.ModelResource, cToken).ConfigureAwait(false)).ToImmutableArray();

                if (hostDependencies.Any())
                {
                    await CreateTunnelDependentContainerAsync(cr, hostDependencies, cctx, signalServicesSpecReadyOnce, cToken).ConfigureAwait(false);
                }
                else
                {
                    // There will be no tunnel services for this container; we have complete information about services this container will need.
                    signalServicesSpecReadyOnce();

                    await CreateDcpContainerAsync(cr, logger, cToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cToken.IsCancellationRequested)
            {
                // Expected cancellation during shutdown - propagate clean cancellation
                throw;
            }
            catch (FailedToApplyEnvironmentException)
            {
                // For this exception we don't want the noise of the stack trace, we've already
                // provided more detail where we detected the issue (e.g. envvar name). To get
                // more diagnostic information reduce logging level for DCP log category to Debug.
                await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cToken, KnownResourceTypes.Container, cr.ModelResource, cr.DcpResourceName)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create container resource {ResourceName}", cr.ModelResource.Name);
                await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cToken, KnownResourceTypes.Container, cr.ModelResource, cr.DcpResourceName)).ConfigureAwait(false);
            }
        }
        finally
        {
            signalServicesSpecReadyOnce();
            AspireEventSource.Instance.DcpObjectCreationStop(dcpContainer.Kind, dcpContainer.Metadata.Name);
        }
    }

    private async Task CreateDcpContainerAsync(RenderedModelResource cr, ILogger logger, CancellationToken cToken)
    {
        cToken.ThrowIfCancellationRequested();

        await PublishEndpointAllocatedEventAsync([cr], cToken).ConfigureAwait(false);
        await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cToken, cr.ModelResource)).ConfigureAwait(false);
        // BeforeResourceStarted already published by the caller.

        var dcpContainer = (Container)cr.DcpResource;
        var modelContainer = cr.ModelResource;

        await ApplyBuildArgumentsAsync(dcpContainer, cr.ModelResource, _executionContext.ServiceProvider, cToken).ConfigureAwait(false);

        var spec = dcpContainer.Spec;

        if (cr.ServicesProduced.Count > 0)
        {
            spec.Ports = BuildContainerPorts(cr);
        }

        spec.VolumeMounts = BuildContainerMounts(cr.ModelResource);

        var (runArgs, failedToApplyRunArgs) = await BuildRunArgsAsync(logger, cr.ModelResource, cToken).ConfigureAwait(false);
        if (failedToApplyRunArgs)
        {
            throw new FailedToApplyEnvironmentException();
        }
        spec.RunArgs = runArgs;

        var (configuration, pemCertificates, createFiles) = await BuildContainerConfiguration(cr, logger, cToken).ConfigureAwait(false);
        if (configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException($"Failed to apply configuration to container {cr.ModelResource.Name}", configuration.Exception);
        }

        var args = configuration.Arguments.Select(a => a.Value);
        // modelContainer is not necessarily ContainerResource (can be custom resource that produces a container).
        if (modelContainer is ContainerResource { ShellExecution: true })
        {
            spec.Args = ["-c", $"{string.Join(' ', args)}"];
        }
        else
        {
            spec.Args = args.ToList();
        }
        dcpContainer.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, configuration.Arguments.Select(a => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive)));

        spec.Env = configuration.EnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList();
        spec.CreateFiles = createFiles;
        if (modelContainer is ContainerResource containerResource)
        {
            spec.Command = containerResource.Entrypoint;
        }
        spec.PemCertificates = pemCertificates;

        if (_dcpInfo is not null)
        {
            DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, _options.Value, _dcpInfo);
        }

        await _kubernetesService.CreateAsync(dcpContainer, cToken).ConfigureAwait(false);

        var containerExes = _appResources.OfType<RenderedModelResource>().Where(ar => ar.DcpResource is ContainerExec ce && ce.Spec.ContainerName == dcpContainer.Metadata.Name).ToArray();
        if (containerExes.Length > 0)
        {
            await CreateContainerExecutablesAsync(containerExes, cToken).ConfigureAwait(false);
        }
    }

    private async Task CreateTunnelDependentContainerAsync(RenderedModelResource cr, ImmutableArray<HostResourceWithEndpoints> hostDependencies, ContainerCreationContext cctx, Action signalServicesSpecReadyOnce, CancellationToken cToken)
    {
        cToken.ThrowIfCancellationRequested();

        // Ensure that we have services and tunnel definitions for all host dependencies of this container.

        List<ContainerNetworkService> newServices = [];
        foreach (var dep in hostDependencies)
        {
            var cnetServices = CreateContainerNetworkServicesForHostResource(dep);
            newServices.AddRange(cnetServices);
        }
        if (newServices.Count > 0)
        {
            foreach (var s in newServices)
            {
                await cctx.ContainerServicesChan.Writer.WriteAsync(s, cToken).ConfigureAwait(false);
            }
        }

        signalServicesSpecReadyOnce();
        await cctx.CreateTunnel.ConfigureAwait(false);

        await CreateDcpContainerAsync(cr, _loggerService.GetLogger(cr.ModelResource), cToken).ConfigureAwait(false);
    }

    private async Task<(IExecutionConfigurationResult, ContainerPemCertificates?, List<ContainerCreateFileSystem>?)>
    BuildContainerConfiguration(RenderedModelResource cr, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        var certificatesDestination = ContainerCertificatePathsAnnotation.DefaultCustomCertificatesDestination;
        var bundlePaths = ContainerCertificatePathsAnnotation.DefaultCertificateBundlePaths.ToList();
        var certificateDirsPaths = ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths.ToList();

        if (cr.ModelResource.TryGetLastAnnotation<ContainerCertificatePathsAnnotation>(out var pathsAnnotation))
        {
            certificatesDestination = pathsAnnotation.CustomCertificatesDestination ?? certificatesDestination;
            bundlePaths = pathsAnnotation.DefaultCertificateBundles ?? bundlePaths;
            certificateDirsPaths = pathsAnnotation.DefaultCertificateDirectories ?? certificateDirsPaths;
        }

        var serverAuthCertificatesBasePath = $"{certificatesDestination}/private";

        var configuration = await ExecutionConfigurationBuilder.Create(cr.ModelResource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .WithCertificateTrustConfig(scope =>
            {
                var dirs = new List<string> { certificatesDestination + "/certs" };
                if (scope == CertificateTrustScope.Append)
                {
                    // When appending to the default trust store, include the default certificate directories
                    dirs.AddRange(certificateDirsPaths!);
                }

                return new()
                {
                    CertificateBundlePath = ReferenceExpression.Create($"{certificatesDestination}/cert.pem"),
                    // Build Linux PATH style colon-separated list of directories
                    CertificateDirectoriesPath = ReferenceExpression.Create($"{string.Join(':', dirs)}"),
                    RootCertificatesPath = certificatesDestination,
                    IsContainer = true,
                };
            })
            .WithHttpsCertificateConfig(cert => new()
            {
                CertificatePath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.crt"),
                KeyPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.key"),
                PfxPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.pfx"),
            })
            .AddExecutionConfigurationGatherer(new OtlpEndpointReferenceGatherer())
            .BuildAsync(_executionContext, resourceLogger, cancellationToken)
            .ConfigureAwait(false);

        List<ContainerFileSystemEntry> customBundleFiles = new();

        // Add the certificates to the Container spec so they'll be placed in the DCP config
        ContainerPemCertificates? pemCertificates = null;
        if (configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var certificateTrustConfiguration)
            && certificateTrustConfiguration.Scope != CertificateTrustScope.None
            && certificateTrustConfiguration.Certificates.Count > 0)
        {
            pemCertificates = new ContainerPemCertificates
            {
                Certificates = certificateTrustConfiguration.Certificates.Select(c =>
                {
                    return new PemCertificate
                    {
                        Thumbprint = c.Thumbprint,
                        Contents = c.ExportCertificatePem(),
                    };
                }).DistinctBy(cert => cert.Thumbprint).ToList(),
                Destination = certificatesDestination,
                ContinueOnError = true,
            };

            if (certificateTrustConfiguration.Scope != CertificateTrustScope.Append)
            {
                // If overriding the default resource CA bundle, then we want to copy our bundle to the well-known locations
                // used by common Linux distributions to make it easier to ensure applications pick it up.
                // Group by common directory to avoid creating multiple file system entries for the same root directory.
                pemCertificates.OverwriteBundlePaths = bundlePaths;
            }

            foreach (var bundleFactory in certificateTrustConfiguration.CustomBundlesFactories)
            {
                var bundleId = bundleFactory.Key;
                var bundleBytes = await bundleFactory.Value(certificateTrustConfiguration.Certificates, cancellationToken).ConfigureAwait(false);

                customBundleFiles.Add(new ContainerFileSystemEntry
                {
                    Name = bundleId,
                    Type = ContainerFileSystemEntryType.File,
                    RawContents = Convert.ToBase64String(bundleBytes),
                });
            }
        }

        var buildCreateFilesContext = new BuildCreateFilesContext
        {
            Resource = cr.ModelResource,
            CertificateTrustScope = certificateTrustConfiguration?.Scope ?? CertificateTrustScope.None,
            CertificateTrustBundlePath = $"{certificatesDestination}/cert.pem",
        };

        if (configuration.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var tlsCertificateConfiguration))
        {
            var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
            buildCreateFilesContext.HttpsCertificateContext = new ContainerFileSystemCallbackHttpsCertificateContext
            {
                CertificatePath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{thumbprint}.crt"),
                KeyPath = tlsCertificateConfiguration.KeyPathReference,
                PfxPath = tlsCertificateConfiguration.PfxPathReference,
                Password = tlsCertificateConfiguration.Password,
            };
        }

        // Build files that need to be created inside the container
        var createFiles = await BuildCreateFilesAsync(
            buildCreateFilesContext,
            cancellationToken).ConfigureAwait(false);

        if (customBundleFiles.Count > 0)
        {
            createFiles.Add(new ContainerCreateFileSystem
            {
                Destination = certificatesDestination,
                Entries = [
                    new ContainerFileSystemEntry
                    {
                        Name = "bundles",
                        Type = ContainerFileSystemEntryType.Directory,
                        Entries = customBundleFiles,
                    },
                ],
            });
        }

        if (tlsCertificateConfiguration is not null)
        {
            var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
            var publicCertificatePem = tlsCertificateConfiguration.Certificate.ExportCertificatePem();
            (var keyPem, var pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(tlsCertificateConfiguration.Certificate, tlsCertificateConfiguration.Password, tlsCertificateConfiguration.IsKeyPathReferenced, tlsCertificateConfiguration.IsPfxPathReferenced, cancellationToken).ConfigureAwait(false);
            var certificateFiles = new List<ContainerFileSystemEntry>()
            {
                new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".crt",
                    Type = ContainerFileSystemEntryType.File,
                    Contents = new string(publicCertificatePem),
                }
            };

            if (keyPem is not null)
            {
                certificateFiles.Add(new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".key",
                    Type = ContainerFileSystemEntryType.File,
                    Contents = new string(keyPem),
                });

                Array.Clear(keyPem, 0, keyPem.Length);
            }

            if (pfxBytes is not null)
            {
                certificateFiles.Add(new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".pfx",
                    Type = ContainerFileSystemEntryType.File,
                    RawContents = Convert.ToBase64String(pfxBytes),
                });

                Array.Clear(pfxBytes, 0, pfxBytes.Length);
            }

            // Write the certificate and key to the container filesystem
            createFiles.Add(new ContainerCreateFileSystem
            {
                Destination = serverAuthCertificatesBasePath,
                Entries = certificateFiles,
            });
        }

        return (configuration, pemCertificates, createFiles);
    }

    private static async Task ApplyBuildArgumentsAsync(Container dcpContainerResource, IResource modelContainerResource, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (modelContainerResource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } dockerfileBuildAnnotation)
        {
            // If there's a factory, generate the Dockerfile content and write it to the specified path
            await DockerfileHelper.ExecuteDockerfileFactoryAsync(dockerfileBuildAnnotation, modelContainerResource, serviceProvider, cancellationToken).ConfigureAwait(false);

            var dcpBuildArgs = new List<EnvVar>();

            foreach (var buildArgument in dockerfileBuildAnnotation.BuildArguments)
            {
                var valueString = buildArgument.Value switch
                {
                    string stringValue => stringValue,
                    IValueProvider valueProvider => await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false),
                    bool boolValue => boolValue ? "true" : "false",
                    null => null,
                    _ => buildArgument.Value.ToString()
                };

                var dcpBuildArg = new EnvVar()
                {
                    Name = buildArgument.Key,
                    Value = valueString
                };

                dcpBuildArgs.Add(dcpBuildArg);
            }

            dcpContainerResource.Spec.Build = new()
            {
                Context = dockerfileBuildAnnotation.ContextPath,
                Dockerfile = dockerfileBuildAnnotation.DockerfilePath,
                Stage = dockerfileBuildAnnotation.Stage,
                Args = dcpBuildArgs
            };

            var dcpBuildSecrets = new List<BuildContextSecret>();

            foreach (var buildSecret in dockerfileBuildAnnotation.BuildSecrets)
            {
                var valueString = buildSecret.Value switch
                {
                    FileInfo filePath => filePath.FullName,
                    IValueProvider valueProvider => await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("Build secret can only be a parameter or a file.")
                };

                if (buildSecret.Value is FileInfo)
                {
                    var dcpBuildSecret = new BuildContextSecret
                    {
                        Id = buildSecret.Key,
                        Type = "file",
                        Source = valueString
                    };
                    dcpBuildSecrets.Add(dcpBuildSecret);
                }
                else
                {
                    var dcpBuildSecret = new BuildContextSecret
                    {
                        Id = buildSecret.Key,
                        Type = "env",
                        Value = valueString
                    };
                    dcpBuildSecrets.Add(dcpBuildSecret);
                }
            }

            dcpContainerResource.Spec.Build = new()
            {
                Context = dockerfileBuildAnnotation.ContextPath,
                Dockerfile = dockerfileBuildAnnotation.DockerfilePath,
                Stage = dockerfileBuildAnnotation.Stage,
                Args = dcpBuildArgs,
                Secrets = dcpBuildSecrets
            };
        }
    }

    private void AddServicesProducedInfo(IResource modelResource, IAnnotationHolder dcpResource, RenderedModelResource appResource)
    {
        var modelResourceName = "(unknown)";
        try
        {
            modelResourceName = DcpNameGenerator.GetObjectNameForResource(modelResource, _options.Value);
        }
        catch { } // For error messages only, OK to fall back to (unknown)

        var servicesProduced = _appResources.OfType<ServiceWithModelResource>().Where(r => r.ModelResource == modelResource);
        foreach (var sp in servicesProduced)
        {
            var ea = sp.EndpointAnnotation;

            if (modelResource.IsContainer())
            {
                if (ea.TargetPort is null)
                {
                    throw new InvalidOperationException($"The endpoint '{ea.Name}' for container resource '{modelResourceName}' must specify the {nameof(EndpointAnnotation.TargetPort)} value");
                }
            }
            else if (!ea.IsProxied)
            {
                if (HasMultipleReplicas(appResource.DcpResource))
                {
                    throw new InvalidOperationException($"Resource '{modelResourceName}' uses multiple replicas and a proxy-less endpoint '{ea.Name}'. These features do not work together.");
                }

                if (ea.Port is int && ea.Port != ea.TargetPort)
                {
                    throw new InvalidOperationException($"The endpoint '{ea.Name}' for resource '{modelResourceName}' is not using a proxy, and it has a value of {nameof(EndpointAnnotation.Port)} property that is different from the value of {nameof(EndpointAnnotation.TargetPort)} property. For proxy-less endpoints they must match.");
                }
            }
            else
            {
                Debug.Assert(ea.IsProxied);

                if (ea.TargetPort is int && ea.Port is int && ea.TargetPort == ea.Port)
                {
                    throw new InvalidOperationException(
                        $"The endpoint '{ea.Name}' for resource '{modelResourceName}' requested a proxy ({nameof(ea.IsProxied)} is true). Non-container resources cannot be proxied when both {nameof(ea.TargetPort)} and {nameof(ea.Port)} are specified with the same value.");
                }

                if (HasMultipleReplicas(appResource.DcpResource) && ea.TargetPort is int)
                {
                    throw new InvalidOperationException(
                        $"Resource '{modelResourceName}' can have multiple replicas, and it uses endpoint '{ea.Name}' that has {nameof(ea.TargetPort)} property set. Each replica must have a unique port; setting {nameof(ea.TargetPort)} is not allowed.");
                }
            }

            var spAnn = new ServiceProducerAnnotation(sp.Service.Metadata.Name);
            (spAnn.Address, _) = NormalizeTargetHost(ea.TargetHost);
            spAnn.Port = ea.TargetPort;
            dcpResource.AnnotateAsObjectList(CustomResource.ServiceProducerAnnotation, spAnn);
            appResource.ServicesProduced.Add(sp);
        }

        static bool HasMultipleReplicas(CustomResource resource)
        {
            if (resource is Executable exe && exe.Metadata.Annotations.TryGetValue(CustomResource.ResourceReplicaCount, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var replicas) && replicas > 1)
            {
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Normalize the target host to a tuple of (address, binding mode) to a single valid address for
    /// service discovery purposes. A user may have configured an endpoint target host that isn't itself
    /// a valid IP address or hostname that can be resolved by other services or clients. For example,
    /// 0.0.0.0 is considered to mean that the service should bind to all IPv4 addresses. When the target
    /// host indicates that the service should bind to all IPv4 or IPv6 addresses, we instead return
    /// "localhost" as the address as that is a valid address for the .NET dev certificate. The binding mode
    /// is metdata that indicates whether an endpoint is bound to a single address or some set of multiple
    /// addresses on the system.
    /// </summary>
    /// <param name="targetHost">The target host from an EndpointAnnotation</param>
    /// <returns>A tuple of (address, binding mode).</returns>
    private static (string, EndpointBindingMode) NormalizeTargetHost(string targetHost)
    {
        return targetHost switch
        {
            null or "" => (KnownHostNames.Localhost, EndpointBindingMode.SingleAddress), // Default is localhost
            var s when EndpointHostHelpers.IsLocalhostOrLocalhostTld(s) => (KnownHostNames.Localhost, EndpointBindingMode.SingleAddress), // Explicitly set to localhost or .localhost subdomain

            var s when IPAddress.TryParse(s, out var ipAddress) => ipAddress switch // The host is an IP address
            {
                var ip when IPAddress.Any.Equals(ip) => (KnownHostNames.Localhost, EndpointBindingMode.IPv4AnyAddresses), // 0.0.0.0 (IPv4 all addresses)
                var ip when IPAddress.IPv6Any.Equals(ip) => (KnownHostNames.Localhost, EndpointBindingMode.IPv6AnyAddresses), // :: (IPv6 all addresses)
                _ => (s, EndpointBindingMode.SingleAddress), // Any other IP address is returned as-is as that will be the only address the service is bound to
            },
            _ => (KnownHostNames.Localhost, EndpointBindingMode.DualStackAnyAddresses), // Any other target host is treated as binding to all IPv4 AND IPv6 addresses
        };
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

    public async Task StopResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping resource '{ResourceName}'...", resourceReference.DcpResourceName);
        var dcpResource = ((RenderedModelResource)resourceReference).DcpResource;
        bool stopped = false;

        AspireEventSource.Instance.StopResourceStart(dcpResource.Kind, dcpResource.Metadata.Name);
        try
        {
            stopped = await DeleteResourceRetryPipeline.ExecuteAsync(async (resourceName, attemptCancellationToken) =>
            {
                V1Patch patch;
                switch (dcpResource)
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
                        throw new InvalidOperationException($"Unexpected resource type: {dcpResource.Kind}");
                }
            }, resourceReference.DcpResourceName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AspireEventSource.Instance.StopResourceStop(dcpResource.Kind, dcpResource.Metadata.Name);
        }

        if (!stopped)
        {
            throw new InvalidOperationException($"Failed to stop resource '{resourceReference.DcpResourceName}'.");
        }
    }

    public IResourceReference GetResource(string resourceName)
    {
        var matchingResource = _appResources
            .OfType<RenderedModelResource>()
            .Where(r => r.DcpResource is not Service)
            .SingleOrDefault(r => string.Equals(r.DcpResource.Metadata.Name, resourceName, StringComparisons.ResourceName));
        if (matchingResource == null)
        {
            throw new InvalidOperationException($"Resource '{resourceName}' not found.");
        }

        return matchingResource;
    }

    public async Task StartResourceAsync(IResourceReference resourceReference, CancellationToken cancellationToken)
    {
        var appResource = (RenderedModelResource)resourceReference;
        var resourceType = GetResourceType(appResource.DcpResource, appResource.ModelResource);
        var resourceLogger = _loggerService.GetLogger(appResource.DcpResourceName);
        AspireEventSource.Instance.StartResourceStart(appResource.DcpResource.Kind, appResource.DcpResource.Metadata.Name);

        try
        {
            _logger.LogDebug("Starting {ResourceType} '{ResourceName}'.", resourceType, appResource.DcpResourceName);

            // Reset cached callback results so they are re-evaluated on restart.
            ForgetCachedCallbackResults(appResource.ModelResource);

            // Raise event after resource has been deleted. This is required because the event sets the status to "Starting" and resources being
            // deleted will temporarily override the status to a terminal state, such as "Exited".
            switch (appResource.DcpResource)
            {
                case Container c:
                    await EnsureResourceDeletedAsync<Container>(appResource.DcpResourceName).ConfigureAwait(false);

                    // Ensure we explicitly start the container
                    c.Spec.Start = true;

                    await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, appResource.ModelResource)).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, appResource.ModelResource, appResource.DcpResourceName)).ConfigureAwait(false);
                    await CreateDcpContainerAsync(appResource, resourceLogger, cancellationToken).ConfigureAwait(false);
                    break;
                case Executable e:
                    await EnsureResourceDeletedAsync<Executable>(appResource.DcpResourceName).ConfigureAwait(false);

                    await _executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, appResource.ModelResource)).ConfigureAwait(false);
                    await _executorEvents.PublishAsync(new OnResourceStartingContext(cancellationToken, resourceType, appResource.ModelResource, appResource.DcpResourceName)).ConfigureAwait(false);
                    await CreateExecutableAsync(appResource, resourceLogger, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected resource type: {appResource.DcpResource.Kind}");
            }
        }
        catch (FailedToApplyEnvironmentException)
        {
            // For this exception we don't want the noise of the stack trace, we've already
            // provided more detail where we detected the issue (e.g. envvar name). To get
            // more diagnostic information reduce logging level for DCP log category to Debug.
            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, appResource.ModelResource, appResource.DcpResourceName)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start resource {ResourceName}", appResource.ModelResource.Name);
            await _executorEvents.PublishAsync(new OnResourceFailedToStartContext(cancellationToken, resourceType, appResource.ModelResource, appResource.DcpResourceName)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            AspireEventSource.Instance.StartResourceStop(appResource.DcpResource.Kind, appResource.DcpResource.Metadata.Name);
        }

        async Task EnsureResourceDeletedAsync<T>(string resourceName) where T : CustomResource, IKubernetesStaticMetadata
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
    }

    private class BuildCreateFilesContext
    {
        public required IResource Resource { get; init; }
        public CertificateTrustScope CertificateTrustScope { get; init; }
        public string? CertificateTrustBundlePath { get; set; }
        public string? CertificateTrustDirectoriesPath { get; set; }
        public ContainerFileSystemCallbackHttpsCertificateContext? HttpsCertificateContext { get; set; }
    }

    private async Task<List<ContainerCreateFileSystem>> BuildCreateFilesAsync(BuildCreateFilesContext context, CancellationToken cancellationToken)
    {
        var createFiles = new List<ContainerCreateFileSystem>();

        if (context.Resource.TryGetAnnotationsOfType<ContainerFileSystemCallbackAnnotation>(out var createFileAnnotations))
        {
            foreach (var a in createFileAnnotations)
            {
                var entries = await a.Callback(
                    new()
                    {
                        Model = context.Resource,
                        ServiceProvider = _executionContext.ServiceProvider,
                        HttpsCertificateContext = context.HttpsCertificateContext,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (entries?.Any() != true)
                {
                    continue;
                }

                createFiles.Add(new ContainerCreateFileSystem
                {
                    Destination = a.DestinationPath,
                    DefaultOwner = a.DefaultOwner,
                    DefaultGroup = a.DefaultGroup,
                    Umask = (int?)a.Umask,
                    Entries = entries.Select(e => e.ToContainerFileSystemEntry()).ToList(),
                });
            }
        }

        return createFiles;
    }

    private async Task<(List<string>, bool)> BuildRunArgsAsync(ILogger resourceLogger, IResource modelResource, CancellationToken cancellationToken)
    {
        var failedToApplyArgs = false;
        var runArgs = new List<string>();

        await modelResource.ProcessContainerRuntimeArgValues(
            _executionContext,
            (a, ex) =>
            {
                if (ex is not null)
                {
                    failedToApplyArgs = true;
                    resourceLogger.LogCritical(ex, "Failed to apply argument value '{ArgKey}'. A dependency may have failed to start.", a);
                    _logger.LogDebug(ex, "Failed to apply argument value '{ArgKey}' to '{ResourceName}'. A dependency may have failed to start.", a, modelResource.Name);
                }
                else if (a is string s)
                {
                    runArgs.Add(s);
                }
            },
            resourceLogger,
            cancellationToken).ConfigureAwait(false);

        return (runArgs, failedToApplyArgs);
    }

    private static List<ContainerPortSpec> BuildContainerPorts(RenderedModelResource cr)
    {
        var ports = new List<ContainerPortSpec>();

        foreach (var sp in cr.ServicesProduced)
        {
            var ea = sp.EndpointAnnotation;

            var portSpec = new ContainerPortSpec()
            {
                ContainerPort = ea.TargetPort,
            };

            if (!ea.IsProxied && ea.Port is int)
            {
                portSpec.HostPort = ea.Port;
            }

            switch (sp.EndpointAnnotation.Protocol)
            {
                case ProtocolType.Tcp:
                    portSpec.Protocol = PortProtocol.TCP;
                    break;
                case ProtocolType.Udp:
                    portSpec.Protocol = PortProtocol.UDP;
                    break;
            }

            if (sp.EndpointAnnotation.TargetHost != KnownHostNames.Localhost)
            {
                portSpec.HostIP = sp.EndpointAnnotation.TargetHost;
            }

            ports.Add(portSpec);
        }

        return ports;
    }

    private static List<VolumeMount> BuildContainerMounts(IResource container)
    {
        var volumeMounts = new List<VolumeMount>();

        if (container.TryGetContainerMounts(out var containerMounts))
        {
            foreach (var mount in containerMounts)
            {
                var volumeSpec = new VolumeMount
                {
                    Source = mount.Source,
                    Target = mount.Target,
                    Type = mount.Type == ContainerMountType.BindMount ? VolumeMountType.Bind : VolumeMountType.Volume,
                    IsReadOnly = mount.IsReadOnly
                };

                volumeMounts.Add(volumeSpec);
            }
        }

        return volumeMounts;
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

    private record struct HostResourceWithEndpoints
    (
        IResourceWithEndpoints Resource,
        IEnumerable<EndpointAnnotation> Endpoints
    );

    private static HostResourceWithEndpoints? AsHostResourceWithEndpoints(IResource resource)
    {
        if (resource is IResourceWithEndpoints rwe && !resource.IsContainer())
        {
            var endpoints = resource.Annotations.OfType<EndpointAnnotation>();
            if (endpoints.Any())
            {
                return new HostResourceWithEndpoints(rwe, endpoints);
            }
        }

        return null;
    }

    private async Task<IEnumerable<HostResourceWithEndpoints>> GetHostDependenciesAsync(IResource resource, CancellationToken cancellationToken)
    {
        var allDependencies = await ResourceExtensions.GetResourceDependenciesAsync(
            resource,
            _executionContext,
            new ResourceDependencyDiscoveryOptions
            {
                DiscoveryMode = ResourceDependencyDiscoveryMode.DirectOnly,
                CacheAnnotationCallbackResults = true,
            },
            cancellationToken
        ).ConfigureAwait(false);

        // Host dependencies are host network resources with endpoints that containers depend on.
        List<HostResourceWithEndpoints> hostDependencies = [.. allDependencies.Select(AsHostResourceWithEndpoints).OfType<HostResourceWithEndpoints>()];

        // Aspire dashboard is special in the context of Open Telemetry ingestion.
        // OTLP exporters do not refer to the OTLP ingestion endpoint via EndpointReference when the model is constructed
        // by the Aspire app host; the endpoint URL is just read from configuration.
        // If there are containers that are OTLP exporters in the model, we need to project dashboard endpoints into container space.
        if (resource.TryGetAnnotationsOfType<OtlpExporterAnnotation>(out _))
        {
            var maybeDashboard = _model.Resources.Where(r => StringComparers.ResourceName.Equals(r.Name, KnownResourceNames.AspireDashboard))
                    .Select(AsHostResourceWithEndpoints).FirstOrDefault();
            if (maybeDashboard is HostResourceWithEndpoints dashboardResource)
            {
                hostDependencies.Add(dashboardResource);
            }
        }

        return hostDependencies;
    }

    private AppResource CreateTunnelProxyResource(List<TunnelConfiguration>? tunnels)
    {
        Debug.Assert(_options.Value.EnableAspireContainerTunnel, "This method should only be called if the container tunnel feature is enabled.");
        Debug.Assert(!_appResources.Any(ar => ar.DcpResource is ContainerNetworkTunnelProxy), "This method should only be called if a tunnel proxy resource hasn't already been created.");

        var tunnelProxy = ContainerNetworkTunnelProxy.Create(GetTunnelProxyResourceName());
        tunnelProxy.Spec.ContainerNetworkName = KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value;
        tunnelProxy.Spec.Aliases = [ContainerHostName];
        tunnelProxy.Spec.Tunnels = tunnels;
        var tunnelAppResource = new AppResource(tunnelProxy);
        _appResources.Add(tunnelAppResource);
        return tunnelAppResource;
    }

    private string GetTunnelProxyResourceName()
    {
        Debug.Assert(_options.Value.EnableAspireContainerTunnel, "This method should only be called if the container tunnel feature is enabled.");
        return KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value + "-tunnelproxy";
    }

    private async Task PublishEndpointAllocatedEventAsync(
        IEnumerable<RenderedModelResource> resource,
        CancellationToken ct)
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
