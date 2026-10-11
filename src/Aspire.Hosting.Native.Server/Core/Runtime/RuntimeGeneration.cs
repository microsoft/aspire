// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Runtime;

/// <summary>Owns execution observations and requests independently of declaration storage and protocol DTOs.</summary>
internal sealed class RuntimeGeneration(ApplicationSnapshot declarations, IWorkloadExecutor executor) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RuntimeResource> _resources = declarations.Resources.ToDictionary(
        resource => resource.Handle.ResourceId, resource => new RuntimeResource(resource));
    private TaskCompletionSource _changed = CreateSignal();
    private long _version = 1;
    private bool _disposed;
    private readonly Dictionary<Guid, ResourceWorkload> _workloads = [];
    private readonly List<Task> _retiredWorkloads = [];
    private readonly HashSet<Guid> _replacingWorkloads = [];

    public RuntimeGeneration(ApplicationSnapshot declarations) : this(declarations, UnavailableWorkloadExecutor.Instance)
    {
    }

    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return Task.WhenAll(_workloads.Values.Select(workload => workload.Completion).Concat(_retiredWorkloads));
            }
        }
    }

    /// <summary>Maps a validated declaration to its stable execution identity.</summary>
    internal ResourceHandle ResolveResource(string name, string typeId)
    {
        lock (_gate)
        {
            EnsureAlive();
            var resource = _resources.Values.SingleOrDefault(resource =>
                string.Equals(resource.Declaration.Name, name, StringComparison.OrdinalIgnoreCase) &&
                resource.Declaration.TypeId == typeId)
                ?? throw new ArgumentException("The declaration has no compatible resource execution.");

            return resource.Declaration.Handle;
        }
    }

    /// <summary>Accepts a complete desired graph while retaining compatible execution owners and workloads.</summary>
    internal Task ApplyRevision(ApplicationSnapshot revision)
    {
        lock (_gate)
        {
            EnsureAlive();
            // A replacement owns a launch plan for the current desired revision.
            // Retry the staged commit after it finishes rather than starting a
            // stale plan or removing a resource while its lease is being replaced.
            if (_replacingWorkloads.Count != 0)
            {
                throw new InvalidOperationException("A resource workload replacement is still being applied.");
            }
            var previous = _resources.Values.ToDictionary(resource => resource.Declaration.Name, StringComparer.OrdinalIgnoreCase);
            var handles = revision.Resources.ToDictionary(resource => resource.Handle.ResourceId, resource =>
                previous.TryGetValue(resource.Name, out var current) && current.Declaration.TypeId == resource.TypeId
                    ? current.Declaration.Handle : new ResourceHandle(GenerationId, Guid.NewGuid()));
            var next = new Dictionary<Guid, RuntimeResource>();
            foreach (var declaration in revision.Resources)
            {
                var updated = declaration with
                {
                    Handle = handles[declaration.Handle.ResourceId],
                    Dependencies = declaration.Dependencies.Select(handle => handles[handle.ResourceId]).ToImmutableArray()
                };
                if (_resources.TryGetValue(updated.Handle.ResourceId, out var retained))
                {
                    if (!retained.Declaration.Configuration.SequenceEqual(updated.Configuration) ||
                        !retained.Declaration.Dependencies.SequenceEqual(updated.Dependencies))
                    {
                        retained.ConfigurationRevision++;
                        retained.ConfigurationStatus = "pending";
                        retained.ConfigurationMessage = "";
                        retained.Healthy = false;
                    }
                    retained.Declaration = updated;
                    next.Add(updated.Handle.ResourceId, retained);
                }
                else
                {
                    next.Add(updated.Handle.ResourceId, new RuntimeResource(updated));
                }
            }
            _retiredWorkloads.RemoveAll(task => task.IsCompletedSuccessfully);
            var cleanup = new List<Task>();
            foreach (var removed in _resources.Values.Where(resource => !next.ContainsKey(resource.Declaration.Handle.ResourceId)))
            {
                CancelRequests(removed);
                if (_workloads.Remove(removed.Declaration.Handle.ResourceId, out var workload))
                {
                    workload.Dispose();
                    cleanup.Add(workload.Completion);
                    _retiredWorkloads.Add(workload.Completion);
                }
            }
            _resources.Clear();
            foreach (var (id, resource) in next)
            {
                _resources.Add(id, resource);
            }
            Changed();

            return Task.WhenAll(cleanup);
        }
    }

    internal bool IsResourceRetired(Guid resourceId)
    {
        lock (_gate)
        {
            return _disposed || !_resources.ContainsKey(resourceId);
        }
    }

    internal RuntimeConfigurationRevision ReadConfiguration(Guid resourceId)
    {
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);

            return new RuntimeConfigurationRevision(resource.ConfigurationRevision, resource.AppliedConfigurationRevision,
                resource.ConfigurationStatus, resource.ConfigurationMessage, resource.Declaration.Configuration,
                resource.Declaration.Dependencies);
        }
    }

    internal async Task<RuntimeConfigurationRevision> WaitConfigurationAsync(Guid resourceId, long revision, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds is < 1 or > 30000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }
        // Other publications share this wake signal. Re-check the desired revision
        // instead of treating an unrelated log/health update as configuration work.
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var current = ReadConfiguration(resourceId);
                if (revision < 0 || revision > current.Revision)
                {
                    throw new ArgumentOutOfRangeException(nameof(revision));
                }
                if (revision != current.Revision)
                {
                    return current;
                }
                changed = _changed.Task;
            }
            try
            {
                await changed.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return ReadConfiguration(resourceId);
            }
        }
    }

    internal void CompleteConfiguration(Guid resourceId, long revision, string status, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (status is not ("succeeded" or "failed") || message.Length > 8192)
        {
            throw new ArgumentException("Invalid configuration result.");
        }
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (revision != resource.ConfigurationRevision || resource.ConfigurationStatus != "pending")
            {
                throw new InvalidOperationException("The configuration revision is stale or already completed.");
            }
            resource.ConfigurationStatus = status;
            resource.ConfigurationMessage = message;
            if (status == "succeeded")
            {
                resource.AppliedConfigurationRevision = revision;
            }
            else
            {
                resource.Healthy = false;
            }
            Changed();
        }
    }

    internal Task<WorkloadEndpoint> StartWorkload(Guid resourceId, WorkloadPlan plan)
    {
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (resource.State is "Stopped" or "Failed" || _workloads.ContainsKey(resourceId) || _replacingWorkloads.Contains(resourceId))
            {
                throw new InvalidOperationException("The resource workload cannot start in the current state.");
            }
            var workload = new ResourceWorkload(new WorkloadIdentity(GenerationId, resourceId), plan, executor,
                token => WaitForDependenciesAsync(resourceId, token),
                (state, endpoint) => PublishWorkload(resourceId, state, endpoint),
                (stream, message) => AppendWorkloadLog(resourceId, stream, message));
            _workloads.Add(resourceId, workload);
            resource.State = "Starting";
            Changed();

            return workload.Start();
        }
    }

    private async Task WaitForDependenciesAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                EnsureAlive();
                var dependencies = GetOwnedResource(resourceId).Declaration.Dependencies
                    .Select(handle => GetResource(handle.ResourceId)).ToArray();
                if (dependencies.Any(dependency => dependency.State is "Failed" or "Stopped"))
                {
                    throw new InvalidOperationException("A workload dependency has terminated.");
                }
                if (dependencies.All(dependency => dependency.Healthy))
                {
                    return;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<WorkloadEndpoint> ReplaceWorkloadAsync(Guid resourceId, long revision, WorkloadPlan plan)
    {
        ResourceWorkload previous;
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (revision != resource.ConfigurationRevision)
            {
                throw new InvalidOperationException("The replacement configuration revision is stale.");
            }
            if (!_workloads.TryGetValue(resourceId, out previous!) || !_replacingWorkloads.Add(resourceId))
            {
                throw new InvalidOperationException("The resource has no workload or a replacement is already pending.");
            }
            resource.State = "Starting";
            resource.Healthy = false;
            resource.Urls = [];
            resource.Commands.Clear();
            CancelRequests(resource);
            InvalidateDependents(resourceId);
            previous.Dispose();
            Changed();
        }
        try
        {
            // Never overlap two leases for one resource. Keep the old workload in
            // the cleanup inventory until its executor confirms actual removal.
            await previous.Completion.ConfigureAwait(false);
            Task<WorkloadEndpoint> started;
            lock (_gate)
            {
                EnsureAlive();
                var resource = GetOwnedResource(resourceId);
                _workloads.Remove(resourceId);
                _replacingWorkloads.Remove(resourceId);
                resource.State = "Unknown";
                started = StartWorkload(resourceId, plan);
                _replacingWorkloads.Add(resourceId);
            }

            return await started.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _replacingWorkloads.Remove(resourceId);
            }
        }
    }

    private void InvalidateDependents(Guid resourceId)
    {
        var affected = new HashSet<Guid> { resourceId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var resource in _resources.Values)
            {
                var id = resource.Declaration.Handle.ResourceId;
                if (!affected.Contains(id) && resource.Declaration.Dependencies.Any(handle => affected.Contains(handle.ResourceId)))
                {
                    affected.Add(id);
                    resource.ConfigurationRevision++;
                    resource.ConfigurationStatus = "pending";
                    resource.ConfigurationMessage = "";
                    resource.Healthy = false;
                    changed = true;
                }
            }
        }
        Changed();
    }

    private void PublishWorkload(Guid resourceId, string state, WorkloadEndpoint? endpoint)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (!_resources.TryGetValue(resourceId, out var resource))
            {
                return;
            }
            if (resource.OwnerDisconnected)
            {
                return;
            }
            resource.State = state;
            resource.Healthy = false;
            resource.Urls = endpoint is { Port: > 0 } ? [$"tcp://{endpoint.Host}:{endpoint.Port}"] : [];
            if (state is "Stopped" or "Failed")
            {
                CancelRequests(resource);
                resource.Commands.Clear();
            }
            Changed();
        }
    }

    private void AppendWorkloadLog(Guid resourceId, string stream, string message)
    {
        lock (_gate)
        {
            if (!_disposed && _resources.TryGetValue(resourceId, out var resource) && !resource.OwnerDisconnected &&
                resource.State is not ("Stopped" or "Failed"))
            {
                AppendLog(resourceId, stream, message);
            }
        }
    }

    public Guid GenerationId => declarations.GenerationId;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsRetired
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public RuntimeResourceOwner ClaimResource(ResourceHandle handle)
    {
        lock (_gate)
        {
            EnsureAlive();
            if (handle.GenerationId != GenerationId || !_resources.TryGetValue(handle.ResourceId, out var resource))
            {
                throw new ArgumentException("The resource is not in this execution.", nameof(handle));
            }
            if (resource.Claimed)
            {
                throw new InvalidOperationException("The execution already has an integration owner.");
            }
            resource.Claimed = true;

            return new RuntimeResourceOwner(this, handle.ResourceId);
        }
    }

    public RuntimeSnapshot Read()
    {
        lock (_gate)
        {
            EnsureAlive();

            return Snapshot();
        }
    }

    internal RuntimeSnapshot ReadDependencies(Guid resourceId)
    {
        lock (_gate)
        {
            EnsureAlive();
            var dependencies = GetOwnedResource(resourceId).Declaration.Dependencies
                .Select(handle => handle.ResourceId).ToHashSet();
            var snapshot = Snapshot();

            return snapshot with { Resources = snapshot.Resources.Where(resource => dependencies.Contains(resource.ResourceId)).ToImmutableArray() };
        }
    }

    public async Task<RuntimeSnapshot> WaitForChangeAsync(long version, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds is < 1 or > 30000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }
        Task changed;
        lock (_gate)
        {
            EnsureAlive();
            if (version < 0 || version > _version)
            {
                throw new ArgumentOutOfRangeException(nameof(version));
            }
            if (version != _version)
            {
                return Snapshot();
            }
            // Capture the signal under the snapshot gate. A publication between
            // reading a version and subscribing must never be missed.
            changed = _changed.Task;
        }
        using var delayCancellation = new CancellationTokenSource();
        await Task.WhenAny(changed, Task.Delay(timeoutMilliseconds, delayCancellation.Token)).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);

        return Read();
    }

    public RuntimeLogBatch ReadLogs(Guid resourceId, long afterSequence)
    {
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetResource(resourceId);
            if (afterSequence < 0 || afterSequence > resource.LogSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(afterSequence));
            }
            var first = resource.Logs.TryPeek(out var entry) ? entry.Sequence : resource.LogSequence + 1;

            return new RuntimeLogBatch(resource.LogSequence, afterSequence < first - 1,
                resource.Logs.Where(line => line.Sequence > afterSequence).ToImmutableArray());
        }
    }

    public RuntimeRequest RequestCommand(string resourceName, string commandName)
    {
        lock (_gate)
        {
            EnsureAlive();
            var resource = _resources.Values.SingleOrDefault(resource =>
                string.Equals(resource.Declaration.Name, resourceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("The resource is not in this execution.", nameof(resourceName));
            if (!resource.Commands.ContainsKey(commandName) || resource.OwnerDisconnected ||
                resource.ConfigurationStatus != "succeeded")
            {
                throw new InvalidOperationException("The resource command is unavailable.");
            }
            EnsureRequestCapacity(resource);
            var request = new RuntimeRequest(commandName);
            resource.PendingCommands.Add(request.Id, request);
            Changed();

            return request;
        }
    }

    public ImmutableArray<RuntimePrompt> ReadInteractions()
    {
        lock (_gate)
        {
            EnsureAlive();

            return _resources.Values.SelectMany(resource => resource.Interactions.Values
                .Select(request => new RuntimePrompt(request.Id, resource.Declaration.Name, request.Name)))
                .OrderBy(prompt => prompt.Id).ToImmutableArray();
        }
    }

    public void RespondInteraction(Guid requestId, bool accepted)
    {
        lock (_gate)
        {
            EnsureAlive();
            foreach (var resource in _resources.Values)
            {
                if (resource.Interactions.Remove(requestId, out var request))
                {
                    request.Complete(new RuntimeRequestResult(accepted ? "accepted" : "rejected", ""));
                    Changed();
                    return;
                }
            }
            throw new ArgumentException("The interaction is unknown or already completed.", nameof(requestId));
        }
    }

    internal void Publish(Guid resourceId, string state, bool healthy, ImmutableArray<string> urls) =>
        NativeDiagnostics.Execute("runtime.resource.publish", null, GenerationId, operation =>
        {
            operation.SetResource(resourceId);
            if (state is not ("Starting" or "Running" or "Failed" or "Stopped") ||
                (healthy && state != "Running") || urls.Length > 32 ||
                urls.Any(url => url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    !string.IsNullOrEmpty(uri.UserInfo)))
            {
                throw new ArgumentException("Invalid resource observation.");
            }
            lock (_gate)
            {
                EnsureAlive();
                var resource = GetOwnedResource(resourceId);
                if (healthy && resource.AppliedConfigurationRevision != resource.ConfigurationRevision)
                {
                    throw new InvalidOperationException("Apply the desired configuration before publishing healthy.");
                }
                if (resource.State is "Stopped" or "Failed")
                {
                    throw new InvalidOperationException("A terminal resource execution cannot publish another observation.");
                }
                resource.State = state;
                resource.Healthy = healthy;
                resource.Urls = state is "Stopped" or "Failed" ? [] : urls;
                if (state is "Stopped" or "Failed")
                {
                    CancelRequests(resource);
                    resource.Commands.Clear();
                }
                Changed();
            }
        });

    internal void AppendLog(Guid resourceId, string stream, string message)
    {
        if (stream is not ("stdout" or "stderr") || message.Length > 8192)
        {
            throw new ArgumentException("Invalid resource log entry.");
        }
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (resource.State is "Stopped" or "Failed")
            {
                throw new InvalidOperationException("The resource log stream is complete.");
            }
            if (resource.Logs.Count == 256)
            {
                resource.Logs.Dequeue();
            }
            resource.Logs.Enqueue(new RuntimeLogEntry(++resource.LogSequence, DateTimeOffset.UtcNow, stream, message));
            Changed();
        }
    }

    internal void DefineCommand(Guid resourceId, string name, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (name.Length > 128 || displayName.Length > 256)
        {
            throw new ArgumentException("The command exceeds the size limit.");
        }
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (resource.State is "Stopped" or "Failed" || resource.Commands.Count >= 32)
            {
                throw new InvalidOperationException("Commands cannot be registered in the current state.");
            }
            if (!resource.Commands.TryAdd(name, displayName))
            {
                throw new InvalidOperationException("The command is already registered.");
            }
            Changed();
        }
    }

    internal ImmutableArray<RuntimeCommand> ReadCommands(Guid resourceId)
    {
        lock (_gate)
        {
            EnsureAlive();

            return GetOwnedResource(resourceId).PendingCommands.Values
                .Select(request => new RuntimeCommand(request.Id, request.Name)).ToImmutableArray();
        }
    }

    internal async Task<ImmutableArray<RuntimeCommand>> WaitCommandsAsync(Guid resourceId, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds is < 1 or > 30000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }
        Task changed;
        lock (_gate)
        {
            EnsureAlive();
            if (GetOwnedResource(resourceId).PendingCommands.Count > 0)
            {
                return ReadCommands(resourceId);
            }
            changed = _changed.Task;
        }
        using var delayCancellation = new CancellationTokenSource();
        await Task.WhenAny(changed, Task.Delay(timeoutMilliseconds, delayCancellation.Token)).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);

        return ReadCommands(resourceId);
    }

    internal void CompleteCommand(Guid resourceId, Guid requestId, string status, string message)
    {
        if (status is not ("succeeded" or "failed" or "cancelled") || message.Length > 8192)
        {
            throw new ArgumentException("Invalid command result.");
        }
        lock (_gate)
        {
            EnsureAlive();
            if (!GetOwnedResource(resourceId).PendingCommands.Remove(requestId, out var request))
            {
                throw new ArgumentException("The command is unknown or already completed.", nameof(requestId));
            }
            request.Complete(new RuntimeRequestResult(status, message));
            Changed();
        }
    }

    internal RuntimeRequest RequestInteraction(Guid resourceId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 8192)
        {
            throw new ArgumentException("The interaction exceeds the size limit.", nameof(message));
        }
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (resource.State is "Stopped" or "Failed")
            {
                throw new InvalidOperationException("Interactions cannot be requested by a terminal resource.");
            }
            EnsureRequestCapacity(resource);
            var request = new RuntimeRequest(message);
            resource.Interactions.Add(request.Id, request);
            Changed();

            return request;
        }
    }

    internal void DisconnectOwner(Guid resourceId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (!_resources.TryGetValue(resourceId, out var resource))
            {
                return;
            }
            if (resource.OwnerDisconnected)
            {
                return;
            }
            resource.OwnerDisconnected = true;
            if (_workloads.TryGetValue(resourceId, out var workload))
            {
                workload.Dispose();
            }
            if (resource.State is not ("Stopped" or "Failed"))
            {
                resource.State = "Failed";
            }
            resource.Healthy = false;
            resource.Urls = [];
            resource.Commands.Clear();
            CancelRequests(resource);
            Changed();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var workload in _workloads.Values)
            {
                workload.Dispose();
            }
            foreach (var resource in _resources.Values)
            {
                CancelRequests(resource);
            }
            _resources.Clear();
            _changed.TrySetResult();
        }
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    private RuntimeResource GetResource(Guid id) => _resources.TryGetValue(id, out var resource)
        ? resource : throw new ArgumentException("The resource is not in this execution.", nameof(id));
    private RuntimeResource GetOwnedResource(Guid id)
    {
        // A call can be awaiting changes when a revision removes its owner.
        // That is expired authority, not invalid caller-supplied arguments.
        ObjectDisposedException.ThrowIf(!_resources.TryGetValue(id, out var resource), this);
        if (!resource.Claimed || resource.OwnerDisconnected)
        {
            throw new InvalidOperationException("The integration no longer owns this execution.");
        }

        return resource;
    }

    private RuntimeSnapshot Snapshot() => new(_version, GenerationId, _resources.Values
        .OrderBy(resource => resource.Declaration.Name, StringComparer.Ordinal)
        .Select(resource => new RuntimeResourceSnapshot(resource.Declaration.Handle.ResourceId,
            resource.Declaration.Name, resource.Declaration.TypeId, resource.State, resource.Healthy, resource.Urls,
            resource.Commands.OrderBy(command => command.Key, StringComparer.Ordinal)
                .Select(command => new RuntimeCommandDefinition(command.Key, command.Value)).ToImmutableArray(),
            resource.ConfigurationRevision, resource.AppliedConfigurationRevision, resource.ConfigurationStatus))
        .ToImmutableArray());

    private void Changed()
    {
        _version++;
        var changed = _changed;
        _changed = CreateSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void EnsureRequestCapacity(RuntimeResource resource)
    {
        if (resource.PendingCommands.Count + resource.Interactions.Count >= 64)
        {
            throw new InvalidOperationException("The resource request limit was reached.");
        }
    }
    private static void CancelRequests(RuntimeResource resource)
    {
        foreach (var request in resource.PendingCommands.Values.Concat(resource.Interactions.Values))
        {
            request.Complete(new RuntimeRequestResult("cancelled", ""));
        }
        resource.PendingCommands.Clear();
        resource.Interactions.Clear();
    }

    private sealed class RuntimeResource(ResourceSnapshot declaration)
    {
        public ResourceSnapshot Declaration { get; set; } = declaration;
        public long ConfigurationRevision { get; set; } = 1;
        public long AppliedConfigurationRevision { get; set; } = declaration.Configuration.IsEmpty ? 1 : 0;
        public string ConfigurationStatus { get; set; } = declaration.Configuration.IsEmpty ? "succeeded" : "pending";
        public string ConfigurationMessage { get; set; } = "";
        public bool Claimed { get; set; }
        public bool OwnerDisconnected { get; set; }
        public string State { get; set; } = "Unknown";
        public bool Healthy { get; set; }
        public ImmutableArray<string> Urls { get; set; } = [];
        public long LogSequence { get; set; }
        public Queue<RuntimeLogEntry> Logs { get; } = [];
        public Dictionary<string, string> Commands { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, RuntimeRequest> PendingCommands { get; } = [];
        public Dictionary<Guid, RuntimeRequest> Interactions { get; } = [];
    }
}

/// <summary>Represents exclusive integration authority over one resource execution.</summary>
internal sealed class RuntimeResourceOwner(RuntimeGeneration generation, Guid resourceId) : IDisposable
{
    private int _disposed;
    public bool IsRevoked => Volatile.Read(ref _disposed) != 0 || generation.IsResourceRetired(resourceId);
    public Guid ResourceId => resourceId;
    public RuntimeSnapshot ReadDependencies()
    {
        EnsureAlive();

        return generation.ReadDependencies(resourceId);
    }
    public RuntimeConfigurationRevision ReadConfiguration()
    {
        EnsureAlive();

        return generation.ReadConfiguration(resourceId);
    }
    public Task<RuntimeConfigurationRevision> WaitConfigurationAsync(long revision, int timeoutMilliseconds)
    {
        EnsureAlive();

        return generation.WaitConfigurationAsync(resourceId, revision, timeoutMilliseconds);
    }
    public void CompleteConfiguration(long revision, string status, string message)
    {
        EnsureAlive();
        generation.CompleteConfiguration(resourceId, revision, status, message);
    }
    public Task<WorkloadEndpoint> StartWorkload(WorkloadPlan plan)
    {
        EnsureAlive();

        return generation.StartWorkload(resourceId, plan);
    }
    public Task<WorkloadEndpoint> ReplaceWorkloadAsync(long revision, WorkloadPlan plan)
    {
        EnsureAlive();

        return generation.ReplaceWorkloadAsync(resourceId, revision, plan);
    }
    public void Publish(string state, bool healthy, ImmutableArray<string> urls)
    {
        EnsureAlive();
        generation.Publish(resourceId, state, healthy, urls);
    }
    public void AppendLog(string stream, string message)
    {
        EnsureAlive();
        generation.AppendLog(resourceId, stream, message);
    }
    public void DefineCommand(string name, string displayName)
    {
        EnsureAlive();
        generation.DefineCommand(resourceId, name, displayName);
    }
    public ImmutableArray<RuntimeCommand> ReadCommands()
    {
        EnsureAlive();

        return generation.ReadCommands(resourceId);
    }
    public Task<ImmutableArray<RuntimeCommand>> WaitCommandsAsync(int timeoutMilliseconds)
    {
        EnsureAlive();

        return generation.WaitCommandsAsync(resourceId, timeoutMilliseconds);
    }
    public void CompleteCommand(Guid id, string status, string message)
    {
        EnsureAlive();
        generation.CompleteCommand(resourceId, id, status, message);
    }
    public RuntimeRequest RequestInteraction(string message)
    {
        EnsureAlive();

        return generation.RequestInteraction(resourceId, message);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            generation.DisconnectOwner(resourceId);
        }
    }
    private void EnsureAlive() => ObjectDisposedException.ThrowIf(IsRevoked, this);
}

/// <summary>Tracks a bounded resource request until completion or owner retirement.</summary>
internal sealed class RuntimeRequest(string name)
{
    private readonly TaskCompletionSource<RuntimeRequestResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; } = name;
    public Task<RuntimeRequestResult> Completion => _completion.Task;
    internal void Complete(RuntimeRequestResult result) => _completion.TrySetResult(result);
}

internal sealed record RuntimeSnapshot(long Version, Guid GenerationId, ImmutableArray<RuntimeResourceSnapshot> Resources);
internal sealed record RuntimeResourceSnapshot(Guid ResourceId, string Name, string TypeId, string State, bool Healthy,
    ImmutableArray<string> Urls, ImmutableArray<RuntimeCommandDefinition> Commands,
    long ConfigurationRevision, long AppliedConfigurationRevision, string ConfigurationStatus);
internal sealed record RuntimeCommandDefinition(string Name, string DisplayName);
internal sealed record RuntimeLogEntry(long Sequence, DateTimeOffset Timestamp, string Stream, string Message);
internal sealed record RuntimeLogBatch(long LastSequence, bool Truncated, ImmutableArray<RuntimeLogEntry> Entries);
internal sealed record RuntimeCommand(Guid Id, string Name);
internal sealed record RuntimePrompt(Guid Id, string ResourceName, string Message);
internal sealed record RuntimeRequestResult(string Status, string Message);
internal sealed record RuntimeConfigurationRevision(long Revision, long AppliedRevision, string Status, string Message,
    ImmutableArray<ResourceConfigurationEntry> Properties, ImmutableArray<ResourceHandle> Dependencies);
