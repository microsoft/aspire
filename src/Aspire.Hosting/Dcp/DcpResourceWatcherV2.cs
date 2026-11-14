// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Shared.ConsoleLogs;
using k8s;
using Microsoft.Extensions.Logging;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Watches a single V2 namespace and owns its snapshot and console-log projections.
/// </summary>
internal sealed class DcpResourceWatcherV2(
    IKubernetesService kubernetesService,
    ResourceLoggerService loggerService,
    DcpExecutorEvents events,
    IReadOnlyList<DcpWorkloadV2> workloads,
    IReadOnlyList<DcpServiceV2> services,
    string namespaceName,
    ILogger logger,
    CancellationToken shutdownToken) : IConsoleLogsService
{
    private readonly ConcurrentDictionary<(string Kind, string Name), CustomResource> _observed = new();
    private readonly ConcurrentDictionary<string, V2.ServiceEndpoint> _endpoints = new();
    private readonly ConcurrentDictionary<string, V2.LogicalService> _observedServices = new();
    private readonly Dictionary<(string Kind, string Name), string> _retired = [];
    private readonly Dictionary<string, LogStream> _streams = [];
    private readonly HashSet<LogStream> _activeStreams = [];
    private readonly Dictionary<string, long> _logCursors = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _publication = new(1, 1);
    private Task? _watchTask;

    private ResourceSnapshotBuilderV2? _snapshotBuilder;
    public ResourceSnapshotBuilderV2 SnapshotBuilder => _snapshotBuilder ??= new(workloads, services, _endpoints, _observedServices);

    public void Start()
    {
        loggerService.SetConsoleLogsService(this);
        _watchTask = Task.WhenAll(
            WatchAsync<V2.LogicalProcess>(ProcessWorkloadAsync),
            WatchAsync<V2.LogicalContainer>(ProcessWorkloadAsync),
            WatchAsync<V2.LogicalContainerExec>(ProcessWorkloadAsync),
            WatchAsync<V2.LogicalService>(ProcessServiceAsync),
            WatchAsync<V2.ServiceEndpoint>(ProcessEndpointAsync),
            WatchSubscribersAsync());
    }

    internal ResourceStatus? GetObservedStatus(CustomResource resource) =>
        _observed.TryGetValue((resource.Kind, resource.Metadata.Name), out var observed)
            ? ResourceSnapshotBuilderV2.GetStatus(observed) : null;

    public void Retire(CustomResource resource)
    {
        lock (_gate)
        {
            var key = (resource.Kind, resource.Metadata.Name);
            var uid = _observed.TryGetValue(key, out var observed) ? observed.Metadata.Uid : resource.Metadata.Uid;
            if (!string.IsNullOrEmpty(uid))
            {
                _retired[key] = uid;
            }
            _observed.TryRemove(key, out _);
            CancelStreams(resource.Metadata.Name);
            foreach (var cursor in _logCursors.Keys.Where(k => k.StartsWith($"{uid}/", StringComparison.Ordinal)).ToArray())
            {
                _logCursors.Remove(cursor);
            }
        }
    }

    private async Task WatchAsync<T>(Func<WatchEventType, T, Task> handler) where T : CustomResource, IKubernetesStaticMetadata
    {
        try
        {
            await DcpPipelineBuilder.BuildWatchResourcePipeline(logger).ExecuteAsync(async ct =>
            {
                await foreach (var (eventType, resource) in kubernetesService.WatchAsync<T>(namespaceName, ct).ConfigureAwait(false))
                {
                    if (eventType == WatchEventType.Bookmark)
                    {
                        continue;
                    }
                    await _publication.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await handler(eventType, resource).ConfigureAwait(false);
                    }
                    finally
                    {
                        _publication.Release();
                    }
                }
                ct.ThrowIfCancellationRequested();
                throw new EndOfStreamException($"DCP V2 {T.ObjectKind} watch ended.");
            }, shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "DCP V2 {Kind} watch terminated unexpectedly.", T.ObjectKind);
            throw;
        }
    }

    private async Task ProcessWorkloadAsync<T>(WatchEventType eventType, T resource) where T : CustomResource
    {
        var workload = workloads.FirstOrDefault(w => w.DcpResourceKind == resource.Kind && w.DcpResourceName == resource.Metadata.Name);
        if (workload is null)
        {
            return;
        }
        CustomResource? previous;
        ResourceStatus status;
        Task[] terminalStreams;
        lock (_gate)
        {
            var key = (resource.Kind, resource.Metadata.Name);
            if (_retired.TryGetValue(key, out var uid) && uid == resource.Metadata.Uid)
            {
                return;
            }
            _observed.TryGetValue(key, out previous);
            if (previous is not null && previous.Metadata.Uid != resource.Metadata.Uid)
            {
                if (eventType == WatchEventType.Deleted)
                {
                    return;
                }
                _retired[key] = previous.Metadata.Uid;
                CancelStreams(resource.Metadata.Name);
                previous = null;
            }
            if (eventType == WatchEventType.Deleted)
            {
                _observed.TryRemove(key, out _);
                CancelStreams(resource.Metadata.Name);
                return;
            }
            if (previous?.Metadata.Uid == resource.Metadata.Uid &&
                previous.Metadata.ResourceVersion == resource.Metadata.ResourceVersion)
            {
                return;
            }
            _observed[key] = resource;
            status = ResourceSnapshotBuilderV2.GetStatus(resource);
            if (workload.ExplicitStart && status.State is null or "Starting" or "Unknown")
            {
                status = status with { State = KnownResourceStates.NotStarted };
            }
            var conditions = resource switch
            {
                V2.LogicalProcess p => p.Status?.Conditions,
                V2.LogicalContainer c => c.Status?.Conditions,
                V2.LogicalContainerExec e => e.Status?.Conditions,
                _ => null
            };
            if (status.State == KnownResourceStates.FailedToStart)
            {
                loggerService.GetLogger(workload.ModelResource).LogError("DCP V2 {Kind} failed: {Conditions}",
                    resource.Kind, string.Join("; ", conditions?.Select(c => $"{c.Reason}: {c.Message}") ?? []));
            }
            UpdateStreams(resource);
            terminalStreams = status.State is not null && KnownResourceStates.TerminalStates.Contains(status.State)
                ? _activeStreams.Where(s => s.ResourceName == workload.DcpResourceName).Select(s => s.Task).ToArray()
                : [];
        }
        if (terminalStreams.Length > 0)
        {
            try
            {
                await Task.WhenAll(terminalStreams).WaitAsync(TimeSpan.FromSeconds(5), shutdownToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogDebug("DCP V2 terminal log flush timed out for {Name}.", workload.DcpResourceName);
            }
        }
        Task publish;
        lock (_gate)
        {
            if (_retired.TryGetValue((resource.Kind, resource.Metadata.Name), out var uid) && uid == resource.Metadata.Uid)
            {
                return;
            }
            // Publish synchronously under the incarnation lock, but await callbacks outside it.
            // ResourceStopped callbacks can call StartResourceAsync and retire this UID.
            publish = events.PublishAsync(new OnResourceChangedContext(shutdownToken, workload.ResourceType,
                workload.ModelResource, workload.DcpResourceName, status,
                previous is null ? null : ResourceSnapshotBuilderV2.GetStatus(previous).State,
                snapshot => SnapshotBuilder.ToSnapshot(resource, workload.ModelResource, snapshot)));
        }
        await publish.ConfigureAwait(false);
    }

    private async Task ProcessServiceAsync(WatchEventType eventType, V2.LogicalService service)
    {
        if (eventType == WatchEventType.Deleted)
        {
            if (_observedServices.TryGetValue(service.Metadata.Name, out var previous) && previous.Metadata.Uid == service.Metadata.Uid)
            {
                _observedServices.TryRemove(service.Metadata.Name, out _);
            }
        }
        else
        {
            _observedServices[service.Metadata.Name] = service;
        }
        await RefreshSnapshotsAsync().ConfigureAwait(false);
    }

    private async Task ProcessEndpointAsync(WatchEventType eventType, V2.ServiceEndpoint endpoint)
    {
        if (eventType == WatchEventType.Deleted)
        {
            if (_endpoints.TryGetValue(endpoint.Metadata.Name, out var previous) && previous.Metadata.Uid == endpoint.Metadata.Uid)
            {
                _endpoints.TryRemove(endpoint.Metadata.Name, out _);
            }
        }
        else
        {
            _endpoints[endpoint.Metadata.Name] = endpoint;
        }
        await RefreshSnapshotsAsync().ConfigureAwait(false);
    }

    private async Task RefreshSnapshotsAsync()
    {
        foreach (var workload in workloads)
        {
            Task? publish = null;
            lock (_gate)
            {
                if (_observed.TryGetValue((workload.DcpResourceKind, workload.DcpResourceName), out var resource) &&
                    (!_retired.TryGetValue((resource.Kind, resource.Metadata.Name), out var uid) || uid != resource.Metadata.Uid))
                {
                    var status = ResourceSnapshotBuilderV2.GetStatus(resource);
                    publish = events.PublishAsync(new OnResourceChangedContext(shutdownToken, workload.ResourceType,
                        workload.ModelResource, workload.DcpResourceName, status, status.State,
                        snapshot => SnapshotBuilder.ToSnapshot(resource, workload.ModelResource, snapshot)));
                }
            }
            if (publish is not null)
            {
                await publish.ConfigureAwait(false);
            }
        }
    }

    private async Task WatchSubscribersAsync()
    {
        try
        {
            await foreach (var change in loggerService.WatchAnySubscribersAsync(shutdownToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    var resource = _observed.Values.FirstOrDefault(r => r.Metadata.Name == change.Name);
                    if (resource is not null)
                    {
                        UpdateStreams(resource);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Application shutdown.
        }
    }

    private void UpdateStreams(CustomResource resource)
    {
        if (!loggerService.HasActiveSubscribers(resource.Metadata.Name))
        {
            CancelStreams(resource.Metadata.Name);
            return;
        }
        var sources = ResourceLogSources.GetSources(resource).ToArray();
        var identities = sources.Select(source => ResourceLogSources.GetIdentity(resource, source)).ToHashSet(StringComparer.Ordinal);
        foreach (var previous in _streams.Where(s => s.Value.ResourceName == resource.Metadata.Name && !identities.Contains(s.Key)).ToArray())
        {
            previous.Value.Cancel();
            _streams.Remove(previous.Key);
            _logCursors.Remove(previous.Key);
        }
        foreach (var source in sources)
        {
            var identity = ResourceLogSources.GetIdentity(resource, source);
            if (_streams.ContainsKey(identity))
            {
                continue;
            }
            var state = new LogStream(resource.Metadata.Name, CancellationTokenSource.CreateLinkedTokenSource(shutdownToken));
            _streams.Add(identity, state);
            _activeStreams.Add(state);
            state.Task = FollowAsync(resource, source, identity, state);
        }
    }

    private async Task FollowAsync(CustomResource resource, string source, string identity, LogStream state)
    {
        // Start outside the watch publication stack: log I/O must not synchronously call user code
        // while a resource observation is being committed.
        await Task.Yield();
        var ct = state.Cancellation.Token;
        try
        {
            long skip;
            lock (_gate)
            {
                skip = _logCursors.GetValueOrDefault(identity);
            }
            var stream = resource switch
            {
                V2.LogicalProcess p => await kubernetesService.GetLogStreamAsync(p, source, ct, timestamps: true, skip: skip).ConfigureAwait(false),
                V2.LogicalContainer c => await kubernetesService.GetLogStreamAsync(c, source, ct, timestamps: true, skip: skip).ConfigureAwait(false),
                V2.LogicalContainerExec e => await kubernetesService.GetLogStreamAsync(e, source, ct, timestamps: true, skip: skip).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unexpected V2 workload '{resource.Kind}'.")
            };
            await using var streamLifetime = stream.ConfigureAwait(false);
            await foreach (var line in ResourceLogSource<V2.LogicalProcess>.ReadLogLinesAsync(stream, ct).ConfigureAwait(false))
            {
                var entry = ToLogEntry(line, source);
                lock (_gate)
                {
                    if (ct.IsCancellationRequested ||
                        !_observed.TryGetValue((resource.Kind, resource.Metadata.Name), out var current) ||
                        current.Metadata.Uid != resource.Metadata.Uid ||
                        ResourceLogSources.GetIdentity(current, source) != identity)
                    {
                        return;
                    }
                    loggerService.AddLogEntries(resource.Metadata.Name, [entry], inMemorySource: false, skipExisting: false);
                    _logCursors[identity] = _logCursors.GetValueOrDefault(identity) + 1;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Subscriber detached, capture changed, or application stopped.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to follow DCP V2 {Source} logs for {Name}.", source, resource.Metadata.Name);
            lock (_gate)
            {
                // Retain the cursor so a changed observation or subscriber can retry without replaying output.
                if (_streams.GetValueOrDefault(identity) == state)
                {
                    _streams.Remove(identity);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _activeStreams.Remove(state);
                state.Complete();
            }
        }
    }

    private static LogEntry ToLogEntry(string line, string source)
    {
        var isError = source == V2.Logs.Stderr;
        string? raw;
        DateTime? timestamp = null;
        if (source == V2.Logs.System && DcpLogParser.TryParseDcpLog(line, out var message, out _, out var error, out var systemTimestamp))
        {
            line = DcpLogParser.FormatSystemLog(ResourceLogSource<V2.LogicalProcess>.NormalizeCarriageReturns(message), null);
            timestamp = systemTimestamp?.UtcDateTime;
            raw = timestamp is null ? line : $"{timestamp.Value:o} {line}";
            isError = error;
        }
        else
        {
            line = ResourceLogSource<V2.LogicalProcess>.NormalizeCarriageReturns(line);
            raw = line;
            if (TimestampParser.TryParseConsoleTimestamp(line, out var parsed))
            {
                timestamp = parsed.Value.Timestamp.UtcDateTime;
                line = parsed.Value.ModifiedText;
            }
        }
        return LogEntry.Create(timestamp, line, raw ?? line, isError, resourcePrefix: null);
    }

    private void CancelStreams(string name)
    {
        foreach (var pair in _streams.Where(s => s.Value.ResourceName == name).ToArray())
        {
            pair.Value.Cancel();
            _streams.Remove(pair.Key);
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<LogEntry>> GetAllLogsAsync(
        string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resource = _observed.Values.FirstOrDefault(r => r.Metadata.Name == resourceName);
        var logs = resource switch
        {
            V2.LogicalProcess p => new ResourceLogSource<V2.LogicalProcess>(logger, kubernetesService, p, follow: false),
            V2.LogicalContainer c => (IAsyncEnumerable<IReadOnlyList<ResourceLogEntry>>)new ResourceLogSource<V2.LogicalContainer>(logger, kubernetesService, c, follow: false),
            V2.LogicalContainerExec e => new ResourceLogSource<V2.LogicalContainerExec>(logger, kubernetesService, e, follow: false),
            _ => null
        };
        if (logs is not null)
        {
            await foreach (var batch in logs.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return batch.Select(e => ToLogEntry(e.RawContent ?? e.Content, e.IsErrorMessage ? V2.Logs.Stderr : V2.Logs.Stdout)).ToArray();
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = _activeStreams.Select(s => s.Task).ToArray();
            foreach (var state in _activeStreams)
            {
                state.Cancel();
            }
        }
        await Task.WhenAll(tasks.Append(_watchTask ?? Task.CompletedTask)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class LogStream(string resourceName, CancellationTokenSource cancellation)
    {
        public string ResourceName { get; } = resourceName;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        private bool _complete;

        public void Cancel()
        {
            if (!_complete)
            {
                Cancellation.Cancel();
            }
        }

        public void Complete()
        {
            _complete = true;
            Cancellation.Dispose();
        }
    }
}
