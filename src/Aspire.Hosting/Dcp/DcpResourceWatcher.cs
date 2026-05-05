// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Dcp.Model;
using Aspire.Shared.ConsoleLogs;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Watches for DCP resource changes (Executables, Containers, ContainerExecs, Services, Endpoints),
/// updates resource state maps, publishes snapshot updates, and manages log streaming lifecycle.
/// </summary>
internal sealed class DcpResourceWatcher : IConsoleLogsService, IAsyncDisposable
{
    private readonly IKubernetesService _kubernetesService;
    private readonly ResourceLoggerService _loggerService;
    private readonly DcpExecutorEvents _executorEvents;
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly CancellationToken _shutdownToken;

    private readonly DcpResourceState _resourceState;
    private readonly ResourceSnapshotBuilder _snapshotBuilder;

    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Task)> _logStreams = new();
    private Task? _resourceWatchTask;

    private readonly record struct LogInformationEntry(string ResourceName, bool? LogsAvailable, bool? HasSubscribers);
    private readonly Channel<LogInformationEntry> _logInformationChannel = Channel.CreateUnbounded<LogInformationEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    // Internal for testing.
    internal ResiliencePipeline WatchResourceRetryPipeline { get; set; }

    internal ResourceSnapshotBuilder SnapshotBuilder => _snapshotBuilder;

    public DcpResourceWatcher(
        ILogger logger,
        IKubernetesService kubernetesService,
        ResourceLoggerService loggerService,
        DcpExecutorEvents executorEvents,
        DistributedApplicationModel model,
        DcpAppResourceStore appResources,
        IConfiguration configuration,
        CancellationToken shutdownToken)
    {
        _kubernetesService = kubernetesService;
        _loggerService = loggerService;
        _executorEvents = executorEvents;
        _logger = logger;
        _configuration = configuration;
        _shutdownToken = shutdownToken;

        _resourceState = new(model.Resources.ToDictionary(r => r.Name), appResources.Get());
        _snapshotBuilder = new(_resourceState);

        WatchResourceRetryPipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(logger);
    }

    public void Start()
    {
        var outputSemaphore = new SemaphoreSlim(1);

        var cancellationToken = _shutdownToken;
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await StopAsync(cts.Token).ConfigureAwait(false);
    }

    private async Task ProcessResourceChange<T>(WatchEventType watchEventType, T resource, ConcurrentDictionary<string, T> resourceByName, string resourceKind, Func<T, CustomResourceSnapshot, CustomResourceSnapshot> snapshotFactory) where T : CustomResource, IKubernetesStaticMetadata
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
                        _logger.LogTrace("Deleting application model resource {AppResourceName} with {ResourceKind} resource {DcpResourceName}", appModelResource.Name, resourceKind, resource.Metadata.Name);
                    }
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Updating application model resource {AppResourceName} with {ResourceKind} resource {DcpResourceName}", appModelResource.Name, resourceKind, resource.Metadata.Name);
                    }

                    var resourceType = DcpExecutor.GetResourceType(resource, appModelResource);
                    var status = GetResourceStatus(resource);
                    AddDcpResourceObservedEvent(resource, appModelResource, resourceKind, status);
                    await _executorEvents.PublishAsync(new OnResourceChangedContext(_shutdownToken, resourceType, appModelResource, resource.Metadata.Name, status, s => snapshotFactory(resource, s))).ConfigureAwait(false);

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

    internal static ResourceStatus GetResourceStatus(CustomResource resource)
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

    private void AddDcpResourceObservedEvent(CustomResource resource, IResource appModelResource, string resourceKind, ResourceStatus status)
    {
        using var activity = ProfilingTelemetry.StartDcpResourceObserved(
            _configuration,
            appModelResource,
            resourceKind,
            resource.Metadata.Name,
            status.State,
            status.StartupTimestamp,
            status.FinishedTimestamp,
            resource.Metadata.Annotations);
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
                await _executorEvents.PublishAsync(new OnResourceChangedContext(_shutdownToken, resourceKind, appModelResource, resourceName, status, s =>
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
}
