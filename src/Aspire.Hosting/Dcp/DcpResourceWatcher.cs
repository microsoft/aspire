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
using Microsoft.Extensions.Logging;
using Polly;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Watches for DCP resource changes (Executables, Containers, ContainerExecs, Services, Endpoints),
/// updates resource state maps, publishes snapshot updates, and manages log streaming lifecycle.
/// </summary>
internal sealed class DcpResourceWatcher : IConsoleLogsService, IAsyncDisposable
{
    private static readonly TimeSpan s_defaultTerminalLogFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IKubernetesService _kubernetesService;
    private readonly ResourceLoggerService _loggerService;
    private readonly DcpExecutorEvents _executorEvents;
    private readonly ILogger _logger;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly CancellationToken _shutdownToken;
    private TimeSpan _terminalLogFlushTimeout = s_defaultTerminalLogFlushTimeout;

    private readonly DcpResourceState _resourceState;
    private readonly ResourceSnapshotBuilder _snapshotBuilder;

    private readonly ConcurrentDictionary<string, LogStreamState> _logStreams = new();
    private readonly Dictionary<string, PendingFollowLogDeduplication> _pendingFollowLogDeduplications = [];
    private readonly object _pendingFollowLogDeduplicationsLock = new();

    // Last identity and resource version seen for each DCP object, keyed by (object kind, object name).
    // The stable key avoids retaining stale entries when a delete is missed, while the UID distinguishes
    // a recreated object from an unchanged watch replay. See ProcessResourceChange.
    private readonly ConcurrentDictionary<(string Kind, string Name), ObservedResource> _observedResources = new();

    // Holds names of resources that reached terminal state and logs have already been flushed for them.
    // Prevents re-reading DCP's log store every time an already-terminal resource is reported again.
    // Point-in-time FailedToStart reads and incomplete attempts are intentionally not recorded so a later changed
    // terminal notification can retry them. Unchanged watch replays are suppressed before reaching this path.
    private readonly ConcurrentDictionary<string, bool> _allLogsFlushed = new();
    private Task? _resourceWatchTask;

    private readonly record struct LogInformationEntry(string ResourceName, bool? LogsAvailable, bool? HasSubscribers, bool ShouldStartStream);
    private readonly Channel<LogInformationEntry> _logInformationChannel = Channel.CreateUnbounded<LogInformationEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    // Internal for testing.
    internal ResiliencePipeline WatchResourceRetryPipeline { get; set; }

    internal ResourceSnapshotBuilder SnapshotBuilder => _snapshotBuilder;

    // Internal for testing.
    internal Task? GetLogStreamTask(string resourceName)
    {
        return _logStreams.TryGetValue(resourceName, out var logStream) ? logStream.Task : null;
    }

    // Internal for testing.
    internal Func<string?, ValueTask>? BeforeLogBatchDeliveryAsync { get; set; }

    public DcpResourceWatcher(
        ILogger logger,
        IKubernetesService kubernetesService,
        ResourceLoggerService loggerService,
        DcpExecutorEvents executorEvents,
        DistributedApplicationModel model,
        DcpAppResourceStore appResources,
        ProfilingTelemetry profilingTelemetry,
        CancellationToken shutdownToken)
    {
        _kubernetesService = kubernetesService;
        _loggerService = loggerService;
        _executorEvents = executorEvents;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
        _shutdownToken = shutdownToken;

        _resourceState = new(model.Resources.ToDictionary(r => r.Name), appResources.Get());
        _snapshotBuilder = new(_resourceState);
        WatchResourceRetryPipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(logger);
    }

    // Internal for testing.
    internal TimeSpan TerminalLogFlushTimeout
    {
        get => _terminalLogFlushTimeout;
        set
        {
            _terminalLogFlushTimeout = value > TimeSpan.Zero ? value : s_defaultTerminalLogFlushTimeout;
        }
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
                _logInformationChannel.Writer.TryWrite(new(subscribers.Name, LogsAvailable: null, subscribers.AnySubscribers, ShouldStartStream: true));
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
                        if (entry.ShouldStartStream)
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
                    }
                    else
                    {
                        CancelLogStream(entry.ResourceName);
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

        foreach (var (_, logStream) in _logStreams)
        {
            logStream.Cancel();
            tasks.Add(logStream.Task);
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
        var resourceChange = ProcessResourceChange(resourceByName, watchEventType, resource);
        if (resourceChange != ResourceChangeResult.Ignored)
        {
            if (resourceChange is ResourceChangeResult.Deleted or ResourceChangeResult.Replaced)
            {
                ResetResourceLogState(resource.Metadata.Name);
            }

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

                    // DCP resource watches and DCP log streams are independent. For a fast-failing
                    // resource the terminal resource event can arrive before the existing follow log
                    // stream has delivered the final stderr/stdout batches. Publishing that terminal
                    // state unblocks WaitForResourceAsync, and tests often inspect forwarded ILogger
                    // output immediately after the wait completes. Flush here to create a best-effort
                    // happens-before edge between terminal notification and synchronous log subscribers.
                    //
                    // Only do this when a subscriber is active. Without subscribers there is no caller
                    // depending on the ordering, and GetAllAsync can still query DCP's external log
                    // store later without this extra read on every terminal transition. If a subscriber
                    // attaches later, the subscriber information path starts the normal DCP follow stream.
                    //
                    // A successfully completed follow stream needs to run only once per terminal period.
                    // The flush is awaited while holding the watcher's single output semaphore, so repeating
                    // a completed flush would stall all resource watches. A later changed terminal notification
                    // can retry point-in-time FailedToStart reads and incomplete attempts because they are not
                    // recorded as complete. The marker is cleared below if the resource is restarted.
                    var logsAvailable = HasLogsAvailable(resource);
                    var isTerminal = status.State is not null && KnownResourceStates.TerminalStates.Contains(status.State);
                    if (isTerminal)
                    {
                        if (logsAvailable &&
                            _loggerService.HasActiveSubscribers(resource.Metadata.Name) &&
                            !_allLogsFlushed.ContainsKey(resource.Metadata.Name))
                        {
                            var completed = await FlushCurrentLogsAsync(resource, status, _shutdownToken).ConfigureAwait(false);
                            if (completed)
                            {
                                _allLogsFlushed.TryAdd(resource.Metadata.Name, true);
                            }
                        }
                    }
                    else
                    {
                        _allLogsFlushed.TryRemove(resource.Metadata.Name, out _);
                    }

                    await _executorEvents.PublishAsync(new OnResourceChangedContext(_shutdownToken, resourceType, appModelResource, resource.Metadata.Name, status, s => snapshotFactory(resource, s))).ConfigureAwait(false);

                    if (logsAvailable)
                    {
                        // Avoid opening a second follow stream only after a terminal follow flush completed. Timed-out
                        // flushes and point-in-time FailedToStart reads leave the normal stream startable so an existing
                        // subscriber can receive later logs without another subscriber change or resource notification.
                        // A replacement still needs its own stream after its old registration was reset.
                        var shouldStartStream =
                            resourceChange == ResourceChangeResult.Replaced ||
                            !_allLogsFlushed.ContainsKey(resource.Metadata.Name);
                        _logInformationChannel.Writer.TryWrite(new(resource.Metadata.Name, LogsAvailable: true, HasSubscribers: null, ShouldStartStream: shouldStartStream));
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

    private void ResetResourceLogState(string resourceName)
    {
        CancelLogStream(resourceName);

        lock (_pendingFollowLogDeduplicationsLock)
        {
            _pendingFollowLogDeduplications.Remove(resourceName);
        }

        _allLogsFlushed.TryRemove(resourceName, out _);
    }

    private void CancelLogStream(string resourceName)
    {
        if (_logStreams.TryGetValue(resourceName, out var logStream))
        {
            // Keep this registration until cancellation has synchronized with any synchronous batch
            // delivery. Otherwise another stream can claim the same name while the old stream is
            // still publishing a batch that ResourceLogSource yielded before cancellation.
            logStream.Cancel();
            _logStreams.TryRemove(new(resourceName, logStream));
        }
    }

    private async Task<bool> FlushCurrentLogsAsync<T>(T resource, ResourceStatus status, CancellationToken cancellationToken)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        var logEntries = new List<LogEntry>();
        var follow = status.State != KnownResourceStates.FailedToStart;
        var completed = false;

        // The resource watcher serializes all resource-change handling through one semaphore in
        // Start(). A follow stream gives the strongest DCP guarantee for terminal logs, but it is
        // still an external stream: if DCP stalls or the resource disappears mid-stream, waiting
        // forever would block unrelated Container/Executable/Service/Endpoint notifications.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_terminalLogFlushTimeout);

        try
        {
            // Fast-failing resources can publish their terminal state before the follow stream has
            // drained stderr/stdout. Open a follow stream before publishing the terminal state
            // because DCP only completes follow streams after all logs for the resource are known
            // to have been delivered. A non-follow stream is only a point-in-time snapshot and can
            // race with DCP's own cleanup/log-drain work.
            //
            // FailedToStart is different: the process never starts, so there may be no completing
            // process log stream to follow. Use a current snapshot there to avoid blocking terminal
            // state publication indefinitely. A later changed terminal notification retries the
            // snapshot because, unlike a completed follow stream, it cannot prove all logs were drained.
            var logSource = new ResourceLogSource<T>(_logger, _kubernetesService, resource, follow: follow);

            // Treat the flush as best-effort: logs collected before the timeout are still forwarded
            // below, then the terminal notification is allowed to proceed.
            await foreach (var batch in logSource.WithCancellation(timeoutCts.Token).ConfigureAwait(false))
            {
                logEntries.AddRange(CreateLogEntries(batch));
            }

            // ResourceLogSource treats cancellation as an expected stream shutdown, so explicitly
            // distinguish that from DCP completing every follow stream.
            timeoutCts.Token.ThrowIfCancellationRequested();
            completed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogDebug("Current log flush for {ResourceName} timed out after {Timeout}.", resource.Metadata.Name, _terminalLogFlushTimeout);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Current log flush for {ResourceName} ended because the resource was deleted.", resource.Metadata.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing current logs for {ResourceName}.", resource.Metadata.Name);
        }

        // These logs came from DCP's external log store, not in-process ILogger. Do not store
        // them as in-memory entries; otherwise GetAllAsync would replay them before querying
        // the same DCP log source again.
        SetPendingFollowLogDeduplication(resource.Metadata.Name, resource.Metadata.Uid, logEntries);
        _loggerService.AddLogEntries(resource.Metadata.Name, logEntries, inMemorySource: false, skipExisting: true);

        // Only normal completion of a follow stream proves DCP has no more logs to deliver.
        return follow && completed;
    }

    private static bool HasLogsAvailable(CustomResource resource)
    {
        return resource is Container { LogsAvailable: true } ||
               resource is Executable { LogsAvailable: true } ||
               resource is ContainerExec { LogsAvailable: true };
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
            if (executable.Spec.Start == false && IsNotStartedExecutableState(executable.Status?.State))
            {
                // If the resource is set for delay start, treat not-yet-started states as NotStarted.
                return new(KnownResourceStates.NotStarted, null, null);
            }

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
        using var activity = _profilingTelemetry.StartDcpResourceObserved(
            appModelResource,
            resourceKind,
            resource.Metadata.Name,
            status.State,
            status.StartupTimestamp,
            status.FinishedTimestamp,
            resource.Metadata.Annotations);
    }

    private static bool IsNotStartedExecutableState(string? state)
    {
        return string.IsNullOrEmpty(state) || state == ExecutableState.Unknown;
    }

    public async IAsyncEnumerable<IReadOnlyList<LogEntry>> GetAllLogsAsync(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<IReadOnlyList<ResourceLogEntry>>? enumerable = null;
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

    private static IEnumerable<LogEntry> CreateLogEntries(IReadOnlyList<ResourceLogEntry> batch)
    {
        foreach (var entry in batch)
        {
            var timestamp = entry.Timestamp;
            var resolvedContent = entry.Content;

            if (timestamp is null && TimestampParser.TryParseConsoleTimestamp(resolvedContent, out var result))
            {
                resolvedContent = result.Value.ModifiedText;
                timestamp = result.Value.Timestamp.UtcDateTime;
            }

            yield return LogEntry.Create(timestamp, resolvedContent, entry.RawContent ?? entry.Content, entry.IsErrorMessage, resourcePrefix: null);
        }
    }

    private void StartLogStream<T>(T resource) where T : CustomResource, IKubernetesStaticMetadata
    {
        IAsyncEnumerable<IReadOnlyList<ResourceLogEntry>>? enumerable = resource switch
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

        var resourceName = resource.Metadata.Name;
        var logStream = new LogStreamState(resource.Metadata.Uid);
        var cancellationToken = logStream.CancellationToken;

        // Register before starting the worker so an immediately completing stream can remove itself.
        if (!_logStreams.TryAdd(resourceName, logStream))
        {
            logStream.Dispose();
            return;
        }

        try
        {
            ObservePendingFollowLogDeduplication(resourceName, logStream);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Starting log streaming for {ResourceName}.", resourceName);
                    }

                    await foreach (var batch in enumerable.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        var logEntries = CreateLogEntries(batch).ToList();
                        if (BeforeLogBatchDeliveryAsync is { } beforeLogBatchDeliveryAsync)
                        {
                            await beforeLogBatchDeliveryAsync(logStream.ResourceUid).ConfigureAwait(false);
                        }

                        logStream.TryDeliver(() =>
                        {
                            logEntries = DeduplicateFollowBatch(resourceName, logStream, logEntries);
                            _loggerService.AddLogEntries(resourceName, logEntries, inMemorySource: false, skipExisting: false);
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                    _logger.LogDebug("Log streaming for {ResourceName} was cancelled.", resourceName);
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Resource was deleted — this is expected for short-lived resources like rebuilders.
                    _logger.LogDebug("Log streaming for {ResourceName} ended because the resource was deleted.", resourceName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error streaming logs for {ResourceName}.", resourceName);
                }
                finally
                {
                    try
                    {
                        try
                        {
                            RemovePendingFollowLogDeduplication(resourceName, logStream);
                        }
                        finally
                        {
                            // Remove only this registration. A canceled stream can finish after a
                            // replacement stream has registered under the same resource name.
                            // LogStreamState intentionally retains reference equality so this
                            // KeyValuePair overload performs an atomic identity-based removal.
                            _logStreams.TryRemove(new(resourceName, logStream));
                        }
                    }
                    finally
                    {
                        logStream.Dispose();
                    }
                }
            });
        }
        catch
        {
            _logStreams.TryRemove(new(resourceName, logStream));
            logStream.Dispose();
            throw;
        }
    }

    private void SetPendingFollowLogDeduplication(string resourceName, string? resourceUid, IReadOnlyList<LogEntry> flushedLogEntries)
    {
        if (flushedLogEntries.Count == 0)
        {
            lock (_pendingFollowLogDeduplicationsLock)
            {
                if (_pendingFollowLogDeduplications.TryGetValue(resourceName, out var pendingDeduplication) &&
                    HasSameResourceIdentity(pendingDeduplication.ResourceUid, resourceUid))
                {
                    RemovePendingFollowLogDeduplication(resourceName, pendingDeduplication);
                }
            }

            return;
        }

        // The terminal flush reads from the same external DCP log store as the normal follow stream.
        // The next follow batch can therefore replay entries that were just flushed. Keep occurrence
        // counts for the flushed entries only; using counts rather than a set preserves legitimate
        // repeated lines while skipping only the overlapping copies.
        var counts = new Dictionary<LogEntryKey, int>();
        DateTime? latestTimestamp = null;
        var remainingCount = 0;

        foreach (var logEntry in flushedLogEntries)
        {
            var key = LogEntryKey.Create(logEntry);
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
            remainingCount++;

            if (logEntry.Timestamp is { } timestamp &&
                (latestTimestamp is null || timestamp > latestTimestamp.Value))
            {
                latestTimestamp = timestamp;
            }
        }

        var newPendingDeduplication = new PendingFollowLogDeduplication(resourceUid, counts, latestTimestamp, remainingCount);
        lock (_pendingFollowLogDeduplicationsLock)
        {
            _pendingFollowLogDeduplications[resourceName] = newPendingDeduplication;
            if (_logStreams.TryGetValue(resourceName, out var logStream) &&
                HasSameResourceIdentity(logStream.ResourceUid, resourceUid))
            {
                logStream.PendingDeduplication = newPendingDeduplication;
            }
        }
    }

    private void ObservePendingFollowLogDeduplication(string resourceName, LogStreamState logStream)
    {
        lock (_pendingFollowLogDeduplicationsLock)
        {
            if (_pendingFollowLogDeduplications.TryGetValue(resourceName, out var pendingDeduplication) &&
                HasSameResourceIdentity(pendingDeduplication.ResourceUid, logStream.ResourceUid))
            {
                logStream.PendingDeduplication = pendingDeduplication;
            }
        }
    }

    private List<LogEntry> DeduplicateFollowBatch(string resourceName, LogStreamState logStream, List<LogEntry> logEntries)
    {
        lock (_pendingFollowLogDeduplicationsLock)
        {
            if (!_pendingFollowLogDeduplications.TryGetValue(resourceName, out var pendingDeduplication) ||
                !HasSameResourceIdentity(pendingDeduplication.ResourceUid, logStream.ResourceUid))
            {
                return logEntries;
            }

            logStream.PendingDeduplication = pendingDeduplication;

            List<LogEntry>? addedEntries = null;
            foreach (var logEntry in logEntries)
            {
                // Consume at most one pending occurrence per matching entry. If a flushed snapshot
                // contained the same line twice, the follow stream must replay it twice before both
                // copies are treated as overlap.
                var key = LogEntryKey.Create(logEntry);
                if (pendingDeduplication.Counts.TryGetValue(key, out var count) && count > 0)
                {
                    pendingDeduplication.Counts[key] = count - 1;
                    pendingDeduplication.RemainingCount--;
                    continue;
                }

                addedEntries ??= [];
                addedEntries.Add(logEntry);
            }

            // Terminal-state snapshots can overlap with the follow stream, but only around the flush.
            // Deduplicate against the flushed snapshot itself instead of rebuilding the full backlog
            // for every follow batch for the lifetime of a chatty resource. DCP log timestamps are
            // monotonic enough for this boundary: once the follow stream yields a newer timestamp, it
            // has moved past the overlap window. Timestamp-less entries cannot establish that boundary,
            // so drop the pending state after the first such batch to avoid suppressing future repeated
            // messages that happen to have the same content.
            if (pendingDeduplication.LatestTimestamp is null ||
                pendingDeduplication.RemainingCount == 0 ||
                logEntries.Any(entry => entry.Timestamp is null || entry.Timestamp > pendingDeduplication.LatestTimestamp.Value))
            {
                RemovePendingFollowLogDeduplication(resourceName, pendingDeduplication);
            }

            return addedEntries ?? [];
        }
    }

    private void RemovePendingFollowLogDeduplication(string resourceName, LogStreamState logStream)
    {
        lock (_pendingFollowLogDeduplicationsLock)
        {
            if (logStream.PendingDeduplication is { } pendingDeduplication)
            {
                RemovePendingFollowLogDeduplication(resourceName, pendingDeduplication);
            }
        }
    }

    private void RemovePendingFollowLogDeduplication(string resourceName, PendingFollowLogDeduplication pendingDeduplication)
    {
        Debug.Assert(Monitor.IsEntered(_pendingFollowLogDeduplicationsLock));

        // Remove only the exact state observed by this stream. A canceled stream from a previous object
        // incarnation can finish after its replacement has already installed new deduplication state.
        if (_pendingFollowLogDeduplications.TryGetValue(resourceName, out var currentDeduplication) &&
            ReferenceEquals(currentDeduplication, pendingDeduplication))
        {
            _pendingFollowLogDeduplications.Remove(resourceName);
        }
    }

    private async Task ProcessEndpointChange(WatchEventType watchEventType, Endpoint endpoint)
    {
        if (ProcessResourceChange(_resourceState.EndpointsMap, watchEventType, endpoint) == ResourceChangeResult.Ignored)
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
        if (ProcessResourceChange(_resourceState.ServicesMap, watchEventType, service) == ResourceChangeResult.Ignored)
        {
            return;
        }

        if (watchEventType is WatchEventType.Added or WatchEventType.Modified)
        {
            DcpModelUtilities.ApplyServiceAddressToEndpoint(service, _resourceState.AppResources);
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

    private ResourceChangeResult ProcessResourceChange<T>(ConcurrentDictionary<string, T> map, WatchEventType watchEventType, T resource)
            where T : CustomResource, IKubernetesStaticMetadata
    {
        var resourceKey = (T.ObjectKind, resource.Metadata.Name);

        switch (watchEventType)
        {
            case WatchEventType.Added:
            case WatchEventType.Modified:
                // DCP watches are torn down and re-established every few minutes (see
                // KubernetesService.WatchAsync, which wraps the watch in PeriodicRestartAsyncEnumerable).
                // A watch is backed by a list-and-watch request, so each fresh watch re-delivers every
                // object that currently exists, and those replays are indistinguishable from real
                // updates. Without this check a resource that never changes again - a container stuck
                // in FailedToStart, for example - keeps producing snapshot versions and keeps re-reading
                // DCP's log store for as long as the AppHost runs.
                // See https://github.com/microsoft/aspire/issues/18869.
                //
                // resourceVersion is the standard mechanism for detecting this: the server changes it
                // whenever the stored object changes and leaves it alone otherwise, so a replay of an
                // unchanged object carries the version we have already seen.
                // https://kubernetes.io/docs/reference/using-api/api-concepts/#resource-versions
                //
                // A delete can occur while the watch is disconnected, so the replacement Added event
                // can arrive without a preceding Deleted event. Kubernetes UIDs identify object
                // incarnations and let that replacement through even if its opaque resource version
                // happens to equal the value recorded for the previous object.
                // https://kubernetes.io/docs/concepts/overview/working-with-objects/names/#uids
                var resourceUid = resource.Metadata.Uid;
                var resourceVersion = resource.Metadata.ResourceVersion;
                var hasPreviousObservation = _observedResources.TryGetValue(resourceKey, out var previousObservation);
                var isSameResource = !hasPreviousObservation || HasSameResourceIdentity(previousObservation.Uid, resourceUid);

                // The value is opaque, so it is only compared for equality; ordering is explicitly not defined. 
                // An empty value means the server did not supply one, which is treated as "cannot tell", 
                // so the event is processed rather than risk suppressing a real change.
                if (isSameResource &&
                    !string.IsNullOrEmpty(resourceVersion) &&
                    string.Equals(previousObservation.ResourceVersion, resourceVersion, StringComparison.Ordinal))
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Ignoring {ResourceKind} resource {ResourceName} reported by the DCP watch because its resource version {ResourceVersion} is unchanged.", T.ObjectKind, resource.Metadata.Name, resourceVersion);
                    }

                    return ResourceChangeResult.Ignored;
                }

                var isReplacement = hasPreviousObservation &&
                    !string.IsNullOrEmpty(previousObservation.Uid) &&
                    !string.IsNullOrEmpty(resourceUid) &&
                    !isSameResource;

                // Added is treated like Modified rather than using TryAdd. A watch restart replays
                // existing objects as Added, and if such an object changed while the watch was down,
                // keeping the stale instance would leave the map disagreeing with both the version
                // recorded here and the snapshot published to subscribers.
                _observedResources[resourceKey] = new(resourceUid, resourceVersion);
                map[resource.Metadata.Name] = resource;
                return isReplacement ? ResourceChangeResult.Replaced : ResourceChangeResult.Updated;

            case WatchEventType.Deleted:
                _observedResources.TryRemove(resourceKey, out _);
                map.Remove(resource.Metadata.Name, out _);
                return ResourceChangeResult.Deleted;

            default:
                return ResourceChangeResult.Ignored;
        }
    }

    private static bool HasSameResourceIdentity(string? previousUid, string? resourceUid)
    {
        return string.Equals(previousUid, resourceUid, StringComparison.Ordinal) ||
            (string.IsNullOrEmpty(previousUid) && string.IsNullOrEmpty(resourceUid));
    }

    private readonly record struct ObservedResource(string? Uid, string? ResourceVersion);

    private enum ResourceChangeResult
    {
        Ignored,
        Updated,
        Replaced,
        Deleted
    }

    private sealed class LogStreamState(string? resourceUid) : IDisposable
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public CancellationToken CancellationToken
        {
            get
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return _cancellation.Token;
                }
            }
        }

        public Task Task => _completion.Task;

        public string? ResourceUid { get; } = resourceUid;

        // Access is guarded by the owning DcpResourceWatcher's pending-deduplication lock.
        public PendingFollowLogDeduplication? PendingDeduplication { get; set; }

        public bool TryDeliver(Action deliver)
        {
            lock (_lock)
            {
                if (_disposed || _cancellation.IsCancellationRequested)
                {
                    return false;
                }

                // Cancellation takes this same lock, so reset cannot free the resource-name slot
                // until a delivery that already started has finished publishing synchronously.
                deliver();
                return true;
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _cancellation.Cancel();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        _cancellation.Dispose();
                        _disposed = true;
                    }
                }
            }
            finally
            {
                _completion.TrySetResult();
            }
        }
    }

    private sealed class PendingFollowLogDeduplication(
        string? resourceUid,
        Dictionary<LogEntryKey, int> counts,
        DateTime? latestTimestamp,
        int remainingCount)
    {
        public string? ResourceUid { get; } = resourceUid;

        public Dictionary<LogEntryKey, int> Counts { get; } = counts;

        public DateTime? LatestTimestamp { get; } = latestTimestamp;

        public int RemainingCount { get; set; } = remainingCount;
    }
}
