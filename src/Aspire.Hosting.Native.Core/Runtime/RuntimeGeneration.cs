// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Runtime;

/// <summary>Owns execution observations and requests independently of declaration storage and protocol DTOs.</summary>
internal sealed partial class RuntimeGeneration(ApplicationSnapshot declarations, IWorkloadExecutor executor, NativeRuntimeOptions options) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RuntimeResource> _resources = declarations.Resources.ToDictionary(
        resource => resource.Handle.ResourceId, resource => new RuntimeResource(resource));
    private TaskCompletionSource _changed = CreateSignal();
    private long _version = 1;
    private bool _disposed;
    private readonly Dictionary<Guid, ResourceWorkload> _workloads = [];
    private readonly List<ResourceWorkload> _retiredWorkloads = [];
    private readonly HashSet<Guid> _replacingWorkloads = [];
    private readonly HashSet<string> _secretRedactions = declarations.SecretParameters
        .SelectMany(value => value.SecretRedactions).ToHashSet(StringComparer.Ordinal);

    public RuntimeGeneration(ApplicationSnapshot declarations) : this(declarations, UnavailableWorkloadExecutor.Instance, new())
    {
    }

    public RuntimeGeneration(ApplicationSnapshot declarations, IWorkloadExecutor executor) : this(declarations, executor, new())
    {
    }

    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                // Ordered retirement reaches producers only after their consumers stop.
                // Observing a producer's original completion here could replay its startup
                // failure even though StopAsync subsequently confirms successful cleanup.
                return Task.WhenAll(_workloads.Where(pair => _declaredRetirement is null || !_declaredOwners.Contains(pair.Key))
                    .Select(pair => pair.Value).Concat(_retiredWorkloads).Select(workload => workload.Completion)
                    .Concat(_declaredRetirement is null ? [] : [_declaredRetirement]));
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
            if (_declaredActivation is { IsCompleted: false })
            {
                throw new InvalidOperationException("The declared application is still activating.");
            }
            // A replacement owns a launch plan for the current desired revision.
            // Retry the staged commit after it finishes rather than starting a
            // stale plan or removing a resource while its lease is being replaced.
            if (_replacingWorkloads.Count != 0)
            {
                throw new InvalidOperationException("A resource workload replacement is still being applied.");
            }
            if (_providerLifecycleStates.Values.Any(state => state.ActiveInvocations != 0))
            {
                throw new InvalidOperationException("A resource provider lifecycle phase is still active.");
            }
            // Keep old credentials while their workload logs can still arrive after
            // replacement. Reject excess history before mutating the desired graph.
            var newRedactions = revision.SecretParameters.SelectMany(value => value.SecretRedactions).ToArray();
            if (_secretRedactions.Concat(newRedactions).Distinct(StringComparer.Ordinal).Count() > 4096)
            {
                throw new SymbolicValueException(SymbolicValueFailure.SecretHistoryLimit);
            }
            if (_declaredActivation is not null)
            {
                // Reject an impossible execution plan before rebinding live
                // declarations or beginning cleanup of removed workloads.
                WorkloadExecutionPlan.Validate(revision.Resources, executor is IWorkloadPreparationExecutor);
            }
            var previous = _resources.Values.ToDictionary(resource => resource.Declaration.Name, StringComparer.OrdinalIgnoreCase);
            var previousDependencies = _resources.ToDictionary(pair => pair.Key,
                pair => pair.Value.Declaration.Dependencies.Select(handle => handle.ResourceId).ToImmutableArray());
            var handles = revision.Resources.ToDictionary(resource => resource.Handle.ResourceId, resource =>
                previous.TryGetValue(resource.Name, out var current) && current.Declaration.TypeId == resource.TypeId
                    ? current.Declaration.Handle : new ResourceHandle(GenerationId, Guid.NewGuid()));
            var next = new Dictionary<Guid, RuntimeResource>();
            foreach (var declaration in revision.Resources)
            {
                var updated = declaration with
                {
                    Handle = handles[declaration.Handle.ResourceId],
                    Dependencies = declaration.Dependencies.Select(handle => handles[handle.ResourceId]).ToImmutableArray(),
                    SymbolicConfiguration = declaration.SymbolicConfiguration.Select(entry => new SymbolicConfigurationEntry(
                        entry.Name, entry.Value.Rebind(GenerationId, handle => handles[handle.ResourceId]))).ToImmutableArray()
                };
                if (_resources.TryGetValue(updated.Handle.ResourceId, out var retained))
                {
                    if (!retained.Declaration.Configuration.SequenceEqual(updated.Configuration) ||
                        !retained.Declaration.Dependencies.SequenceEqual(updated.Dependencies) ||
                        !EquivalentSymbolic(retained.Declaration.SymbolicConfiguration, updated.SymbolicConfiguration) ||
                        !DeclaredWorkload.Equivalent(retained.Declaration.Workload, updated.Workload) ||
                        !retained.Declaration.ProviderLifecycle.Select(binding => (binding.ProviderId, binding.TypeId, binding.Phase, binding.CapabilityId))
                            .SequenceEqual(updated.ProviderLifecycle.Select(binding => (binding.ProviderId, binding.TypeId, binding.Phase, binding.CapabilityId))))
                    {
                        retained.ConfigurationRevision++;
                        retained.ConfigurationStatus = ConfigurationStatus.Pending;
                        retained.ConfigurationMessage = "";
                        retained.Healthy = false;
                    }
                    else if (_providerLifecycleStates.TryGetValue(updated.Handle.ResourceId, out var providerState))
                    {
                        // A retained physical workload adopts the new authoring
                        // object without replaying initialization or restarting.
                        providerState.Bindings = updated.ProviderLifecycle;
                    }
                    retained.Declaration = updated;
                    next.Add(updated.Handle.ResourceId, retained);
                }
                else
                {
                    next.Add(updated.Handle.ResourceId, new RuntimeResource(updated));
                }
            }
            _retiredWorkloads.RemoveAll(workload => workload.Completion.IsCompletedSuccessfully);
            var cleanup = _retiredWorkloads.Select(workload => workload.StopAsync()).ToList();
            var removedWorkloads = new Dictionary<Guid, ResourceWorkload>();
            foreach (var removed in _resources.Values.Where(resource => !next.ContainsKey(resource.Declaration.Handle.ResourceId)))
            {
                CancelRequests(removed);
                if (_workloads.Remove(removed.Declaration.Handle.ResourceId, out var workload))
                {
                    removedWorkloads.Add(removed.Declaration.Handle.ResourceId, workload);
                    _retiredWorkloads.Add(workload);
                }
            }
            _resources.Clear();
            foreach (var secret in newRedactions)
            {
                _secretRedactions.Add(secret);
            }
            foreach (var (id, resource) in next)
            {
                _resources.Add(id, resource);
            }
            Changed();

            // Removed consumers still retire against the previous readiness graph,
            // even though their declarations are absent from the adopted revision.
            cleanup.Add(RetireDeclaredWorkloadsAsync(removedWorkloads, previousDependencies));
            var applying = ApplyDeclaredRevisionAsync(Task.WhenAll(cleanup));
            if (_declaredActivation is not null)
            {
                _declaredActivation = applying;
            }

            return applying;
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

    internal ImmutableArray<SymbolicConfigurationEntry> ReadSymbolicConfiguration(Guid resourceId)
    {
        lock (_gate)
        {
            EnsureAlive();

            return GetOwnedResource(resourceId).Declaration.SymbolicConfiguration;
        }
    }
    internal async Task<RuntimeConfigurationRevision> WaitConfigurationAsync(Guid resourceId, long revision, int timeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);
        // Other publications share this wake signal. Re-check the desired revision
        // instead of treating an unrelated log/health update as configuration work.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds), options.TimeProvider);
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

    internal void CompleteConfiguration(Guid resourceId, long revision, ConfigurationStatus status, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (status is not (ConfigurationStatus.Succeeded or ConfigurationStatus.Failed) || message.Length > 8192)
        {
            throw new ArgumentException("Invalid configuration result.");
        }
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (revision != resource.ConfigurationRevision || resource.ConfigurationStatus != ConfigurationStatus.Pending)
            {
                throw new InvalidOperationException("The configuration revision is stale or already completed.");
            }
            resource.ConfigurationStatus = status;
            resource.ConfigurationMessage = Redact(message);
            if (status == ConfigurationStatus.Succeeded)
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

    internal Task<WorkloadEndpoint> StartWorkload(Guid resourceId, WorkloadPlan plan) =>
        StartWorkloadCore(resourceId, plan, () => plan);

    internal Task<WorkloadEndpoint> StartSymbolicWorkload(Guid resourceId, long revision, WorkloadPlan template,
        ValueNetworkContext context, SymbolicWorkloadBindings bindings)
    {
        WorkloadDefinition.Validate(template);
        bindings.Validate(template);
        lock (_gate)
        {
            ValidateSymbolicBindings(resourceId, revision, template, context, bindings);

            return StartWorkloadCore(resourceId, template, () => ResolveSymbolicWorkload(resourceId, revision, template, context, bindings));
        }
    }

    private void ValidateSymbolicBindings(Guid resourceId, long revision, WorkloadPlan template,
        ValueNetworkContext context, SymbolicWorkloadBindings bindings)
    {
        EnsureAlive();
        var resource = GetOwnedResource(resourceId);
        if (revision != resource.ConfigurationRevision)
        {
            throw new SymbolicValueException(SymbolicValueFailure.StaleRevision);
        }
        var expectedContext = template is ContainerWorkload ? ValueNetworkContext.Container : ValueNetworkContext.Host;
        if (context != expectedContext)
        {
            throw new SymbolicValueException(SymbolicValueFailure.UnsupportedNetworkContext);
        }
        foreach (var key in bindings.Environment.Select(binding => binding.ConfigurationKey)
            .Concat(bindings.Arguments.Select(binding => binding.ConfigurationKey)))
        {
            var value = resource.Declaration.SymbolicConfiguration.SingleOrDefault(entry => entry.Name == key)?.Value
                ?? throw new SymbolicValueException(SymbolicValueFailure.ConfigurationUnavailable);
            // DCP currently allocates host-facing addresses only. Do not reuse a
            // loopback allocation for a container consumer, even for port-only values.
            if (context == ValueNetworkContext.Container && value.References.Any())
            {
                throw new SymbolicValueException(SymbolicValueFailure.UnsupportedNetworkContext);
            }
        }
    }

    private WorkloadPlan ResolveSymbolicWorkload(Guid resourceId, long revision, WorkloadPlan template,
        ValueNetworkContext context, SymbolicWorkloadBindings bindings)
    {
        lock (_gate)
        {
            ValidateSymbolicBindings(resourceId, revision, template, context, bindings);
            var values = GetOwnedResource(resourceId).Declaration.SymbolicConfiguration.ToDictionary(entry => entry.Name, entry => entry.Value);
            string Resolve(string key)
            {
                var symbolic = values[key];
                var value = symbolic.Resolve(context, ResolveEndpointValue);
                if (symbolic.IsSecret)
                {
                    if (!_secretRedactions.Contains(value) && _secretRedactions.Count >= 4096)
                    {
                        throw new SymbolicValueException(SymbolicValueFailure.SecretHistoryLimit);
                    }
                    _secretRedactions.Add(value);
                }

                return value;
            }
            var environment = template.Environment.AddRange(bindings.Environment
                .Select(binding => new WorkloadEnvironment(binding.Name, Resolve(binding.ConfigurationKey))));
            var arguments = template.Arguments.ToBuilder();
            foreach (var binding in bindings.Arguments)
            {
                arguments[binding.Index] = Resolve(binding.ConfigurationKey);
            }
            var plan = SymbolicWorkloadBindings.Copy(template, environment, arguments.ToImmutable());
            WorkloadDefinition.Validate(plan);

            return plan;
        }
    }

    private string ResolveEndpointValue(ResourceHandle handle, string name, EndpointValueProperty property)
    {
        if (handle.GenerationId != GenerationId ||
            !_resources.TryGetValue(handle.ResourceId, out var resource) || resource.Allocation is null ||
            resource.State.IsTerminal() ||
            resource.State != ResourceState.Running && !resource.AllocationPrepared)
        {
            throw new SymbolicValueException(SymbolicValueFailure.EndpointUnavailable);
        }
        var endpoint = resource.Allocation.Endpoints.SingleOrDefault(endpoint => endpoint.Name == name)
            ?? throw new SymbolicValueException(SymbolicValueFailure.EndpointUnavailable);
        var host = endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host;

        return property switch
        {
            EndpointValueProperty.Host => endpoint.Host,
            EndpointValueProperty.Port => endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EndpointValueProperty.HostAndPort => $"{host}:{endpoint.Port}",
            EndpointValueProperty.Scheme => endpoint.UriScheme,
            EndpointValueProperty.Url => new UriBuilder(endpoint.UriScheme, endpoint.Host, endpoint.Port).Uri.AbsoluteUri.TrimEnd('/'),
            _ => throw new ArgumentException("Unsupported symbolic endpoint property.")
        };
    }

    private Task<WorkloadEndpoint> StartWorkloadCore(Guid resourceId, WorkloadPlan plan, Func<WorkloadPlan> resolvePlan)
    {
        WorkloadDefinition.Validate(plan);
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (resource.State.IsTerminal() || _workloads.ContainsKey(resourceId) || _replacingWorkloads.Contains(resourceId))
            {
                throw new InvalidOperationException("The resource workload cannot start in the current state.");
            }
            var lifecycle = CreateLifecycle(resourceId);
            var workload = new ResourceWorkload(new WorkloadIdentity(GenerationId, resourceId), resolvePlan, executor, options,
                token => WaitForDependenciesAsync(resourceId, token),
                (state, endpoint) => PublishWorkload(resourceId, state, endpoint),
                (stream, message) => AppendWorkloadLog(resourceId, stream, message),
                resource.Declaration.Workload is null ? _ => Task.CompletedTask : lifecycle.Initialize,
                resource.Declaration.Workload is null ? (_, _) => Task.CompletedTask : lifecycle.Allocated,
                resource.Declaration.Workload is null ? () => Task.CompletedTask : lifecycle.Retire,
                plan, endpoint => PublishAllocation(resourceId, endpoint),
                token => WaitForValuesAsync(resourceId, token));
            _workloads.Add(resourceId, workload);
            resource.State = ResourceState.Starting;
            resource.Allocation = null;
            resource.AllocationPrepared = false;
            Changed();

            return workload.Start();
        }
    }

    private void PublishAllocation(Guid resourceId, WorkloadEndpoint endpoint)
    {
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            resource.Allocation = endpoint;
            resource.AllocationPrepared = true;
            Changed();
        }
    }

    private async Task WaitForValuesAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                EnsureAlive();
                var resource = GetOwnedResource(resourceId);
                var references = resource.Declaration.SymbolicConfiguration.SelectMany(entry => entry.Value.References)
                    .Where(handle => handle.ResourceId != resourceId).Distinct()
                    .Select(handle => GetResource(handle.ResourceId)).ToArray();
                if (references.Any(source => source.State.IsTerminal()))
                {
                    throw new SymbolicValueException(SymbolicValueFailure.EndpointUnavailable);
                }
                if (references.All(source => source.Allocation is not null))
                {
                    return;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                if (dependencies.Any(dependency => dependency.State.IsTerminal()))
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

    internal Task<WorkloadEndpoint> ReplaceWorkloadAsync(Guid resourceId, long revision, WorkloadPlan plan) =>
        ReplaceWorkloadCoreAsync(resourceId, revision, plan, () => plan);

    internal Task<WorkloadEndpoint> ReplaceSymbolicWorkloadAsync(Guid resourceId, long revision, WorkloadPlan template,
        ValueNetworkContext context, SymbolicWorkloadBindings bindings)
    {
        WorkloadDefinition.Validate(template);
        bindings.Validate(template);
        lock (_gate)
        {
            ValidateSymbolicBindings(resourceId, revision, template, context, bindings);

            return ReplaceWorkloadCoreAsync(resourceId, revision, template, () => ResolveSymbolicWorkload(resourceId, revision, template, context, bindings));
        }
    }

    private async Task<WorkloadEndpoint> ReplaceWorkloadCoreAsync(Guid resourceId, long revision, WorkloadPlan plan, Func<WorkloadPlan> resolvePlan)
    {
        WorkloadDefinition.Validate(plan);
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
            resource.State = ResourceState.Starting;
            resource.Healthy = false;
            resource.Urls = [];
            resource.Commands.Clear();
            CancelRequests(resource);
            InvalidateDependents(resourceId, _declaredOwners.Contains(resourceId));
            previous.Dispose();
            Changed();
        }
        try
        {
            // Never overlap two leases for one resource. Keep the old workload in
            // the cleanup inventory until its executor confirms actual removal.
            await previous.StopAsync().ConfigureAwait(false);
            Task<WorkloadEndpoint> started;
            lock (_gate)
            {
                EnsureAlive();
                var resource = GetOwnedResource(resourceId);
                _workloads.Remove(resourceId);
                _replacingWorkloads.Remove(resourceId);
                resource.State = ResourceState.Unknown;
                started = StartWorkloadCore(resourceId, plan, resolvePlan);
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

    private void InvalidateDependents(Guid resourceId, bool skipPendingRevisions)
    {
        var affected = new HashSet<Guid> { resourceId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var resource in _resources.Values)
            {
                var id = resource.Declaration.Handle.ResourceId;
                if (!affected.Contains(id) && resource.Declaration.Dependencies
                    .Concat(resource.Declaration.SymbolicConfiguration.SelectMany(entry => entry.Value.References))
                    .Any(handle => affected.Contains(handle.ResourceId)))
                {
                    affected.Add(id);
                    if (!skipPendingRevisions || resource.ConfigurationStatus != ConfigurationStatus.Pending)
                    {
                        resource.ConfigurationRevision++;
                    }
                    resource.ConfigurationStatus = ConfigurationStatus.Pending;
                    resource.ConfigurationMessage = "";
                    resource.Healthy = false;
                    changed = true;
                }
            }
        }
        Changed();
    }

    private void PublishWorkload(Guid resourceId, ResourceState state, WorkloadEndpoint? endpoint)
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
            resource.Allocation = endpoint;
            resource.AllocationPrepared = false;
            resource.Healthy = false;
            resource.Urls = endpoint is null ? [] : endpoint.Endpoints.Select(port =>
                new UriBuilder(port.UriScheme, port.Host, port.Port).Uri.AbsoluteUri.TrimEnd('/')).ToImmutableArray();
            if (state.IsTerminal())
            {
                CancelRequests(resource);
                resource.Commands.Clear();
            }
            Changed();
        }
    }

    private void AppendWorkloadLog(Guid resourceId, LogStream stream, string message)
    {
        lock (_gate)
        {
            if (!_disposed && _resources.TryGetValue(resourceId, out var resource) && !resource.OwnerDisconnected &&
                !resource.State.IsTerminal())
            {
                AppendLog(resourceId, stream, message);
            }
        }
    }

    public Guid GenerationId { get; } = declarations.GenerationId;
    public DateTimeOffset CreatedAt { get; } = options.TimeProvider.GetUtcNow();
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
            return new(_version, GenerationId, _resources.Values
                .Where(resource => dependencies.Contains(resource.Declaration.Handle.ResourceId))
                .OrderBy(resource => resource.Declaration.Name, StringComparer.Ordinal)
                .Select(SnapshotResource).ToImmutableArray());
        }
    }

    public async Task<RuntimeSnapshot> WaitForChangeAsync(long version, int timeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);
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
        await Task.WhenAny(changed, Task.Delay(TimeSpan.FromMilliseconds(timeoutMilliseconds), options.TimeProvider, delayCancellation.Token)).ConfigureAwait(false);
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
                resource.ConfigurationStatus != ConfigurationStatus.Succeeded)
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
                    request.Complete(new RuntimeRequestResult(accepted ? RequestOutcome.Accepted : RequestOutcome.Rejected, ""));
                    Changed();
                    return;
                }
            }
            throw new ArgumentException("The interaction is unknown or already completed.", nameof(requestId));
        }
    }

    internal void Publish(Guid resourceId, ResourceState state, bool healthy, ImmutableArray<string> urls) =>
        NativeDiagnostics.Execute("runtime.resource.publish", null, GenerationId, operation =>
        {
            operation.SetResource(resourceId);
            if (state is not (ResourceState.Starting or ResourceState.Running or ResourceState.Failed or ResourceState.Stopped) ||
                (healthy && state != ResourceState.Running) || urls.Length > 32 ||
                urls.Any(url => url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    !string.IsNullOrEmpty(uri.UserInfo)))
            {
                throw new ArgumentException("Invalid resource observation.");
            }
            lock (_gate)
            {
                EnsureAlive();
                var resource = GetOwnedResource(resourceId);
                if (urls.Any(url => Redact(url) != url))
                {
                    throw new ArgumentException("Resource observation URLs cannot contain declared secrets.");
                }
                if (healthy && resource.AppliedConfigurationRevision != resource.ConfigurationRevision)
                {
                    throw new InvalidOperationException("Apply the desired configuration before publishing healthy.");
                }
                if (resource.State.IsTerminal())
                {
                    throw new InvalidOperationException("A terminal resource execution cannot publish another observation.");
                }
                resource.State = state;
                resource.Healthy = healthy;
                resource.Urls = state.IsTerminal() ? [] : urls;
                if (state.IsTerminal())
                {
                    CancelRequests(resource);
                    resource.Commands.Clear();
                }
                Changed();
            }
        }, options.TimeProvider);

    internal void AppendLog(Guid resourceId, LogStream stream, string message)
    {
        if (stream is not (LogStream.Stdout or LogStream.Stderr) || message.Length > 8192)
        {
            throw new ArgumentException("Invalid resource log entry.");
        }
        lock (_gate)
        {
            EnsureAlive();
            var resource = GetOwnedResource(resourceId);
            if (resource.State.IsTerminal())
            {
                throw new InvalidOperationException("The resource log stream is complete.");
            }
            if (resource.Logs.Count >= options.RetainedResourceLogEntries)
            {
                resource.Logs.Dequeue();
            }
            resource.Logs.Enqueue(new RuntimeLogEntry(++resource.LogSequence, options.TimeProvider.GetUtcNow(), stream, Redact(message)));
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
            if (Redact(name) != name)
            {
                throw new ArgumentException("Resource command identities cannot contain declared secrets.");
            }
            var resource = GetOwnedResource(resourceId);
            if (resource.State.IsTerminal() || resource.Commands.Count >= 32)
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
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);
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
        await Task.WhenAny(changed, Task.Delay(TimeSpan.FromMilliseconds(timeoutMilliseconds), options.TimeProvider, delayCancellation.Token)).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);

        return ReadCommands(resourceId);
    }

    internal void CompleteCommand(Guid resourceId, Guid requestId, RequestOutcome status, string message)
    {
        if (status is not (RequestOutcome.Succeeded or RequestOutcome.Failed or RequestOutcome.Cancelled) || message.Length > 8192)
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
            request.Complete(new RuntimeRequestResult(status, Redact(message)));
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
            if (resource.State.IsTerminal())
            {
                throw new InvalidOperationException("Interactions cannot be requested by a terminal resource.");
            }
            EnsureRequestCapacity(resource);
            var request = new RuntimeRequest(Redact(message));
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
            if (!resource.State.IsTerminal())
            {
                resource.State = ResourceState.Failed;
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
            // Repeated retirement is a new cleanup attempt for retained failures.
            _disposed = true;
            var declared = _workloads.Where(pair => _declaredOwners.Contains(pair.Key)).ToDictionary();
            if (_declaredRetirement is null || _declaredRetirement.IsFaulted)
            {
                var resources = _resources.ToDictionary(pair => pair.Key,
                    pair => pair.Value.Declaration.Dependencies.Select(dependency => dependency.ResourceId).ToImmutableArray());
                // Keep the immutable retirement DAG across cleanup retries.
                foreach (var (id, dependencies) in _retirementDependencies)
                {
                    resources.TryAdd(id, dependencies);
                }
                _retirementDependencies = resources;
                _declaredRetirement = RetireDeclaredWorkloadsAsync(declared, resources);
                _ = _declaredRetirement.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            foreach (var workload in _workloads.Where(pair => !_declaredOwners.Contains(pair.Key)).Select(pair => pair.Value).Concat(_retiredWorkloads))
            {
                _ = workload.StopAsync();
            }
            foreach (var resource in _resources.Values)
            {
                CancelRequests(resource);
            }
            _resources.Clear();
            _secretRedactions.Clear();
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
        .Select(SnapshotResource).ToImmutableArray());

    private RuntimeResourceSnapshot SnapshotResource(RuntimeResource resource) =>
        new(resource.Declaration.Handle.ResourceId, resource.Declaration.Name, resource.Declaration.TypeId,
            resource.State, resource.Healthy, resource.Urls,
            resource.Commands.Count == 0 ? [] : resource.Commands.OrderBy(command => command.Key, StringComparer.Ordinal)
                .Select(command => new RuntimeCommandDefinition(command.Key, Redact(command.Value))).ToImmutableArray(),
            resource.ConfigurationRevision, resource.AppliedConfigurationRevision, resource.ConfigurationStatus);

    private void Changed()
    {
        _version++;
        var changed = _changed;
        _changed = CreateSignal();
        changed.TrySetResult();
    }

    private string Redact(string message)
    {
        foreach (var secret in _secretRedactions.OrderByDescending(secret => secret.Length))
        {
            if (secret.Length > 0)
            {
                message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private static bool EquivalentSymbolic(ImmutableArray<SymbolicConfigurationEntry> first,
        ImmutableArray<SymbolicConfigurationEntry> second) => first.Length == second.Length &&
        first.Zip(second).All(pair => pair.First.Name == pair.Second.Name && pair.First.Value.EquivalentTo(pair.Second.Value));

    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void EnsureRequestCapacity(RuntimeResource resource)
    {
        if (resource.PendingCommands.Count + resource.Interactions.Count >= options.MaximumPendingRequestsPerResource)
        {
            throw new InvalidOperationException("The resource request limit was reached.");
        }
    }
    private static void CancelRequests(RuntimeResource resource)
    {
        foreach (var request in resource.PendingCommands.Values.Concat(resource.Interactions.Values))
        {
            request.Complete(new RuntimeRequestResult(RequestOutcome.Cancelled, ""));
        }
        resource.PendingCommands.Clear();
        resource.Interactions.Clear();
    }

    private sealed class RuntimeResource(ResourceSnapshot declaration)
    {
        public ResourceSnapshot Declaration { get; set; } = declaration;
        public long ConfigurationRevision { get; set; } = 1;
        public long AppliedConfigurationRevision { get; set; } = declaration.Configuration.IsEmpty && declaration.SymbolicConfiguration.IsEmpty ? 1 : 0;
        public ConfigurationStatus ConfigurationStatus { get; set; } = declaration.Configuration.IsEmpty && declaration.SymbolicConfiguration.IsEmpty ? ConfigurationStatus.Succeeded : ConfigurationStatus.Pending;
        public string ConfigurationMessage { get; set; } = "";
        public bool Claimed { get; set; }
        public bool OwnerDisconnected { get; set; }
        public ResourceState State { get; set; } = ResourceState.Unknown;
        public bool Healthy { get; set; }
        public ImmutableArray<string> Urls { get; set; } = [];
        public WorkloadEndpoint? Allocation { get; set; }
        public bool AllocationPrepared { get; set; }
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
    public void CompleteConfiguration(long revision, ConfigurationStatus status, string message)
    {
        EnsureAlive();
        generation.CompleteConfiguration(resourceId, revision, status, message);
    }
    public Task<WorkloadEndpoint> StartWorkload(WorkloadPlan plan)
    {
        EnsureAlive();

        return generation.StartWorkload(resourceId, plan);
    }
    public Task<WorkloadEndpoint> StartSymbolicWorkload(long revision, WorkloadPlan template,
        ValueNetworkContext context, SymbolicWorkloadBindings bindings)
    {
        EnsureAlive();

        return generation.StartSymbolicWorkload(resourceId, revision, template, context, bindings);
    }
    public Task<WorkloadEndpoint> ReplaceSymbolicWorkloadAsync(long revision, WorkloadPlan template,
        ValueNetworkContext context, SymbolicWorkloadBindings bindings)
    {
        EnsureAlive();

        return generation.ReplaceSymbolicWorkloadAsync(resourceId, revision, template, context, bindings);
    }

    public ImmutableArray<SymbolicConfigurationEntry> ReadSymbolicConfiguration()
    {
        EnsureAlive();

        return generation.ReadSymbolicConfiguration(resourceId);
    }
    public Task<WorkloadEndpoint> ReplaceWorkloadAsync(long revision, WorkloadPlan plan)
    {
        EnsureAlive();

        return generation.ReplaceWorkloadAsync(resourceId, revision, plan);
    }
    public void Publish(ResourceState state, bool healthy, ImmutableArray<string> urls)
    {
        EnsureAlive();
        generation.Publish(resourceId, state, healthy, urls);
    }
    public void AppendLog(LogStream stream, string message)
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
    public void CompleteCommand(Guid id, RequestOutcome status, string message)
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
internal sealed record RuntimeResourceSnapshot(Guid ResourceId, string Name, string TypeId, ResourceState State, bool Healthy,
    ImmutableArray<string> Urls, ImmutableArray<RuntimeCommandDefinition> Commands,
    long ConfigurationRevision, long AppliedConfigurationRevision, ConfigurationStatus ConfigurationStatus);
internal sealed record RuntimeCommandDefinition(string Name, string DisplayName);
internal sealed record RuntimeLogEntry(long Sequence, DateTimeOffset Timestamp, LogStream Stream, string Message);
internal sealed record RuntimeLogBatch(long LastSequence, bool Truncated, ImmutableArray<RuntimeLogEntry> Entries);
internal sealed record RuntimeCommand(Guid Id, string Name);
internal sealed record RuntimePrompt(Guid Id, string ResourceName, string Message);
internal sealed record RuntimeRequestResult(RequestOutcome Status, string Message);
internal sealed record RuntimeConfigurationRevision(long Revision, long AppliedRevision, ConfigurationStatus Status, string Message,
    ImmutableArray<ResourceConfigurationEntry> Properties, ImmutableArray<ResourceHandle> Dependencies);
