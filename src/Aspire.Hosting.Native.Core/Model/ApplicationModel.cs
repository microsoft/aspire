// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Model;

/// <summary>Owns resource identities and dependency declarations for one application generation.</summary>
internal sealed class ApplicationModel : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Resource> _resources = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SymbolicValue> _secretParameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterSnapshot> _parameters = new(StringComparer.Ordinal);
    private ApplicationSnapshot? _snapshot;
    private bool _disposed;
    private readonly TimeProvider _timeProvider;

    internal ApplicationModel() : this(TimeProvider.System)
    {
    }

    internal ApplicationModel(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        GenerationId = Guid.NewGuid();
    }

    /// <summary>Gets the identity invalidated when this declaration graph is retired.</summary>
    public Guid GenerationId { get; }

    /// <summary>Adds a named resource with a declared ATS type identity.</summary>
    public ResourceHandle AddResource(string name, string typeId) =>
        NativeDiagnostics.Execute("model.resource.add", null, GenerationId, operation => AddResourceCore(name, typeId, false, operation), _timeProvider);

    /// <summary>Adds a compute environment independently of the integration's resource type name.</summary>
    public ResourceHandle AddComputeEnvironment(string name, string typeId) =>
        NativeDiagnostics.Execute("model.resource.add", null, GenerationId, operation => AddResourceCore(name, typeId, true, operation), _timeProvider);

    private ResourceHandle AddResourceCore(string name, string typeId, bool isComputeEnvironment, NativeDiagnostics.Operation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        if (name.Length > 128 || !char.IsAsciiLetter(name[0]) ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException("Resource names must start with an ASCII letter and contain at most 128 letters, digits, or hyphens.", nameof(name));
        }
        var declaredType = new ResourceTypeId(typeId);

        lock (_gate)
        {
            EnsureMutable();
            if (!_names.Add(name))
            {
                throw new InvalidOperationException($"Resource '{name}' is already declared.");
            }
            var handle = new ResourceHandle(GenerationId, Guid.NewGuid());
            _resources.Add(handle.ResourceId, new Resource(handle, name, declaredType) { IsComputeEnvironment = isComputeEnvironment });
            operation.SetResource(handle.ResourceId);

            return handle;
        }
    }

    /// <summary>Adds a readiness dependency without permitting self-dependencies or cycles.</summary>
    public void AddDependency(ResourceHandle resource, ResourceHandle dependency) =>
        NativeDiagnostics.Execute("model.dependency.add", null, GenerationId, operation =>
        {
            operation.SetResource(resource.ResourceId);
            AddDependencyCore(resource, dependency);
        }, _timeProvider);

    private void AddDependencyCore(ResourceHandle resource, ResourceHandle dependency)
    {
        lock (_gate)
        {
            EnsureMutable();
            var target = GetResource(resource);
            var prerequisite = GetResource(dependency);
            var path = DependencyPath(prerequisite, resource.ResourceId);
            if (path is not null)
            {
                var steps = new List<string> { $"{target.Name}.Start" };
                foreach (var id in path)
                {
                    var name = _resources[id].Name;
                    steps.Add($"{name}.Ready");
                    steps.Add($"{name}.Start");
                }
                throw new Runtime.ExecutionPlanException(string.Join(" -> ", steps));
            }
            target.Dependencies.Add(dependency.ResourceId);
        }
    }

    /// <summary>Reads declarations without exposing mutable model storage.</summary>
    public ResourceSnapshot ReadResource(ResourceHandle handle) =>
        NativeDiagnostics.Execute("model.resource.read", null, GenerationId, operation =>
        {
            operation.SetResource(handle.ResourceId);
            return ReadResourceCore(handle);
        }, _timeProvider);

    /// <summary>Captures bounded integration configuration without executing integration code.</summary>
    public void SetConfiguration(ResourceHandle handle, ImmutableArray<ResourceConfigurationEntry> configuration)
    {
        if (configuration.Length > 64 || configuration.Any(entry =>
            entry is null || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 128 ||
            entry.Value is null || entry.Value.Length > 8192) ||
            configuration.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != configuration.Length)
        {
            throw new ArgumentException("Invalid resource configuration.", nameof(configuration));
        }
        lock (_gate)
        {
            EnsureMutable();
            GetResource(handle).Configuration = configuration.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToImmutableArray();
        }
    }

    /// <summary>Declares a named secret once within this composition, retaining its value only on the server.</summary>
    public SymbolicValue AddSecretParameter(string name, string value)
    {
        var parameter = SymbolicValue.SecretParameter(GenerationId, name, value);
        lock (_gate)
        {
            EnsureMutable();
            if (_secretParameters.Count + _parameters.Count >= 64 || _parameters.ContainsKey(name) || !_secretParameters.TryAdd(name, parameter))
            {
                throw new ArgumentException("The secret parameter is duplicate or exceeds capacity.");
            }

            return parameter;
        }
    }

    /// <summary>Declares a configuration parameter and whether its value may be published as a default.</summary>
    public SymbolicValue AddParameter(string name, string value, bool hasDefault)
    {
        var parameter = SymbolicValue.Parameter(GenerationId, name, value);
        lock (_gate)
        {
            EnsureMutable();
            if (_secretParameters.Count + _parameters.Count >= 64 || _secretParameters.ContainsKey(name) ||
                !_parameters.TryAdd(name, new(parameter, hasDefault)))
            {
                throw new ArgumentException("The parameter is duplicate or exceeds capacity.");
            }

            return parameter;
        }
    }

    /// <summary>Declares a deployment input without reading a value on the authoring machine.</summary>
    public SymbolicValue AddUnboundParameter(string name, bool secret)
    {
        var parameter = SymbolicValue.UnboundParameter(GenerationId, name, secret);
        lock (_gate)
        {
            EnsureMutable();
            if (_secretParameters.Count + _parameters.Count >= 64 || _secretParameters.ContainsKey(name) || _parameters.ContainsKey(name))
            {
                throw new ArgumentException("The parameter is duplicate or exceeds capacity.");
            }
            if (secret)
            {
                _secretParameters.Add(name, parameter);
            }
            else
            {
                _parameters.Add(name, new(parameter, false));
            }

            return parameter;
        }
    }

    /// <summary>Assigns compute to an environment without introducing a runtime readiness edge.</summary>
    public void SetComputeEnvironment(ResourceHandle resource, ResourceHandle environment)
    {
        lock (_gate)
        {
            EnsureMutable();
            var compute = GetResource(resource);
            var target = GetResource(environment);
            if (resource == environment || compute.Workload is null || !target.IsComputeEnvironment)
            {
                throw new ArgumentException("Compute assignment requires a workload and a distinct declared compute environment.");
            }
            compute.ComputeEnvironment = environment;
        }
    }

    public void ValidateValue(SymbolicValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            EnsureAlive();
            if (value.GenerationId != GenerationId)
            {
                throw new ArgumentException("The symbolic value belongs to another composition.");
            }
            foreach (var reference in value.References)
            {
                GetResource(reference);
            }
            if (value.SecretParameters.Any(parameter => !_secretParameters.TryGetValue(parameter.ParameterName, out var declared) ||
                !ReferenceEquals(parameter, declared)))
            {
                throw new ArgumentException("The symbolic parameter is not declared in this composition.");
            }
            if (value.Parameters.Where(parameter => !parameter.IsSecret).Any(parameter =>
                !_parameters.TryGetValue(parameter.ParameterName, out var declared) || !ReferenceEquals(parameter, declared.Value)))
            {
                throw new ArgumentException("The symbolic parameter is not declared in this composition.");
            }
        }
    }

    /// <summary>Atomically replaces one symbolic field without turning value availability into resource readiness.</summary>
    public void SetSymbolicConfiguration(ResourceHandle handle, string name, SymbolicValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128)
        {
            throw new ArgumentException("Invalid symbolic configuration name.");
        }
        lock (_gate)
        {
            EnsureMutable();
            ValidateValue(value);
            var resource = GetResource(handle);
            if (resource.SymbolicConfiguration.Count >= 64 && !resource.SymbolicConfiguration.ContainsKey(name))
            {
                throw new ArgumentException("The symbolic configuration exceeds capacity.");
            }
            resource.SymbolicConfiguration[name] = value;
        }
    }

    public void RemoveSymbolicConfiguration(ResourceHandle handle, string name)
    {
        lock (_gate)
        {
            EnsureMutable();
            GetResource(handle).SymbolicConfiguration.Remove(name);
        }
    }

    private ResourceSnapshot ReadResourceCore(ResourceHandle handle)
    {
        lock (_gate)
        {
            EnsureAlive();

            return Snapshot(GetResource(handle));
        }
    }

    /// <summary>Reads an immutable view while preserving composition until sealing.</summary>
    public ApplicationSnapshot Inspect() =>
        NativeDiagnostics.Execute("model.inspect", null, GenerationId, _ => InspectCore(), _timeProvider);

    private ApplicationSnapshot InspectCore()
    {
        lock (_gate)
        {
            EnsureAlive();

            return _snapshot ?? CreateSnapshot();
        }
    }

    /// <summary>Atomically seals composition and returns the same immutable snapshot on subsequent calls.</summary>
    public ApplicationSnapshot Seal() =>
        NativeDiagnostics.Execute("model.seal", null, GenerationId, _ => SealCore(), _timeProvider);

    private ApplicationSnapshot SealCore()
    {
        lock (_gate)
        {
            EnsureAlive();

            return _snapshot ??= CreateSnapshot();
        }
    }

    /// <summary>Retires the generation and rejects all subsequent model access.</summary>
    public void Dispose() =>
        NativeDiagnostics.Execute("model.retire", null, GenerationId, _ => DisposeCore(), _timeProvider);

    private void DisposeCore()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _resources.Clear();
            _names.Clear();
            _secretParameters.Clear();
            _parameters.Clear();
            _snapshot = null;
        }
    }

    private void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EnsureMutable()
    {
        EnsureAlive();
        if (_snapshot is not null)
        {
            throw new InvalidOperationException("The application model is sealed.");
        }
    }

    private Resource GetResource(ResourceHandle handle)
    {
        if (handle.GenerationId != GenerationId)
        {
            throw new ArgumentException("The resource belongs to a different application generation.", nameof(handle));
        }
        if (!_resources.TryGetValue(handle.ResourceId, out var resource))
        {
            throw new ArgumentException("The resource identity is not declared in this application generation.", nameof(handle));
        }

        return resource;
    }

    private List<Guid>? DependencyPath(Resource start, Guid target)
    {
        var parents = new Dictionary<Guid, Guid?> { [start.Handle.ResourceId] = null };
        var pending = new Stack<Guid>();
        pending.Push(start.Handle.ResourceId);
        while (pending.TryPop(out var id))
        {
            if (id == target)
            {
                var path = new List<Guid>();
                for (Guid? current = id; current is not null; current = parents[current.Value])
                {
                    path.Add(current.Value);
                }
                path.Reverse();

                return path;
            }
            foreach (var dependency in Dependencies(_resources[id]))
            {
                if (parents.TryAdd(dependency, id))
                {
                    pending.Push(dependency);
                }
            }
        }

        return null;
    }

    private ApplicationSnapshot CreateSnapshot() => new(GenerationId,
        _resources.Values.OrderBy(resource => resource.Name, StringComparer.Ordinal).Select(Snapshot).ToImmutableArray())
    {
        SecretParameters = _secretParameters.Values.ToImmutableArray(),
        Parameters = _parameters.Values.ToImmutableArray()
    };

    private ResourceSnapshot Snapshot(Resource resource) => new(resource.Handle, resource.Name, resource.TypeId.Value,
        Dependencies(resource).Select(id => _resources[id])
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
            .Select(dependency => dependency.Handle).ToImmutableArray(), resource.Configuration)
    {
        Workload = resource.Workload,
        ComputeEnvironment = resource.ComputeEnvironment,
        IsComputeEnvironment = resource.IsComputeEnvironment,
        ProviderLifecycle = resource.ProviderLifecycle,
        SymbolicConfiguration = resource.SymbolicConfiguration.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SymbolicConfigurationEntry(pair.Key, pair.Value)).ToImmutableArray()
    };

    private static IEnumerable<Guid> Dependencies(Resource resource) => resource.Dependencies;

    /// <summary>Validates and stores desired workload metadata without launching or acquiring runtime ownership.</summary>
    public void SetWorkload(ResourceHandle handle, DeclaredWorkload workload)
    {
        lock (_gate)
        {
            EnsureMutable();
            if (GetResource(handle).IsComputeEnvironment)
            {
                throw new ArgumentException("A compute environment cannot declare a local workload.");
            }
            Runtime.WorkloadDefinition.ValidateDeclaration(workload.Plan);
            workload.Bindings.Validate(workload.Plan);
            GetResource(handle).Workload = workload;
        }
    }

    /// <summary>Atomically updates the current workload and its optional symbolic field before sealing.</summary>
    public void MutateWorkload(ResourceHandle handle,
        Func<DeclaredWorkload, (DeclaredWorkload Workload, SymbolicConfigurationEntry? Configuration)> mutation)
    {
        lock (_gate)
        {
            EnsureMutable();
            var resource = GetResource(handle);
            var current = resource.Workload ?? throw new InvalidOperationException("The resource has no declared workload.");
            var (workload, configuration) = mutation(current);
            Runtime.WorkloadDefinition.ValidateDeclaration(workload.Plan);
            workload.Bindings.Validate(workload.Plan);
            // Validate the complete plan before changing symbolic state so a failed
            // workload or value validation cannot leave a partially applied binding.
            if (configuration is not null)
            {
                SetSymbolicConfiguration(handle, configuration.Name, configuration.Value);
            }
            resource.Workload = workload;
        }
    }

    /// <summary>Attaches only provider bindings already authenticated by the hosting boundary.</summary>
    internal void SetProviderLifecycle(ResourceHandle handle, ImmutableArray<Runtime.ProviderLifecycleBinding> bindings)
    {
        lock (_gate)
        {
            EnsureMutable();
            var resource = GetResource(handle);
            if (!resource.ProviderLifecycle.IsEmpty && !resource.ProviderLifecycle.SequenceEqual(bindings))
            {
                throw new InvalidOperationException("The declaration already has a provider lifecycle owner.");
            }
            resource.ProviderLifecycle = bindings;
        }
    }

    private sealed class Resource(ResourceHandle handle, string name, ResourceTypeId typeId)
    {
        public ResourceHandle Handle { get; } = handle;
        public string Name { get; } = name;
        public ResourceTypeId TypeId { get; } = typeId;
        public HashSet<Guid> Dependencies { get; } = [];
        public ImmutableArray<ResourceConfigurationEntry> Configuration { get; set; } = [];
        public Dictionary<string, SymbolicValue> SymbolicConfiguration { get; } = new(StringComparer.Ordinal);
        public DeclaredWorkload? Workload { get; set; }
        public ResourceHandle? ComputeEnvironment { get; set; }
        public bool IsComputeEnvironment { get; init; }
        public ImmutableArray<Runtime.ProviderLifecycleBinding> ProviderLifecycle { get; set; } = [];
    }
}
