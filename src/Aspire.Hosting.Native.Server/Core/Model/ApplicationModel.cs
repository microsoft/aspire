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
    private ApplicationSnapshot? _snapshot;
    private bool _disposed;

    internal ApplicationModel()
    {
        GenerationId = Guid.NewGuid();
    }

    /// <summary>Gets the identity invalidated when this declaration graph is retired.</summary>
    public Guid GenerationId { get; }

    /// <summary>Adds a named resource with a declared ATS type identity.</summary>
    public ResourceHandle AddResource(string name, string typeId) =>
        NativeDiagnostics.Execute("model.resource.add", null, GenerationId, operation => AddResourceCore(name, typeId, operation));

    private ResourceHandle AddResourceCore(string name, string typeId, NativeDiagnostics.Operation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        if (name.Length > 128 || !char.IsAsciiLetter(name[0]) ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException("Resource names must start with an ASCII letter and contain at most 128 letters, digits, or hyphens.", nameof(name));
        }
        var separator = typeId.IndexOf('/');
        if (separator <= 0 || separator == typeId.Length - 1 ||
            typeId.IndexOf('/', separator + 1) >= 0 || typeId.Any(char.IsWhiteSpace) || typeId.Any(char.IsControl))
        {
            throw new ArgumentException("Resource type identities must have the form 'namespace/type'.", nameof(typeId));
        }

        lock (_gate)
        {
            EnsureMutable();
            if (!_names.Add(name))
            {
                throw new InvalidOperationException($"Resource '{name}' is already declared.");
            }
            var handle = new ResourceHandle(GenerationId, Guid.NewGuid());
            _resources.Add(handle.ResourceId, new Resource(handle, name, typeId));
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
        });

    private void AddDependencyCore(ResourceHandle resource, ResourceHandle dependency)
    {
        lock (_gate)
        {
            EnsureMutable();
            var target = GetResource(resource);
            var prerequisite = GetResource(dependency);
            if (resource == dependency || Reaches(prerequisite, resource.ResourceId))
            {
                throw new InvalidOperationException($"Dependency '{target.Name}' -> '{prerequisite.Name}' would create a cycle.");
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
        });

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
        NativeDiagnostics.Execute("model.inspect", null, GenerationId, _ => InspectCore());

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
        NativeDiagnostics.Execute("model.seal", null, GenerationId, _ => SealCore());

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
        NativeDiagnostics.Execute("model.retire", null, GenerationId, _ => DisposeCore());

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

    private bool Reaches(Resource start, Guid target)
    {
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(start.Handle.ResourceId);
        while (pending.TryPop(out var id))
        {
            if (id == target)
            {
                return true;
            }
            if (!visited.Add(id))
            {
                continue;
            }
            foreach (var dependency in _resources[id].Dependencies)
            {
                pending.Push(dependency);
            }
        }

        return false;
    }

    private ApplicationSnapshot CreateSnapshot() => new(GenerationId,
        _resources.Values.OrderBy(resource => resource.Name, StringComparer.Ordinal).Select(Snapshot).ToImmutableArray());

    private ResourceSnapshot Snapshot(Resource resource) => new(resource.Handle, resource.Name, resource.TypeId,
        resource.Dependencies.Select(id => _resources[id])
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
            .Select(dependency => dependency.Handle).ToImmutableArray(), resource.Configuration);

    private sealed class Resource(ResourceHandle handle, string name, string typeId)
    {
        public ResourceHandle Handle { get; } = handle;
        public string Name { get; } = name;
        public string TypeId { get; } = typeId;
        public HashSet<Guid> Dependencies { get; } = [];
        public ImmutableArray<ResourceConfigurationEntry> Configuration { get; set; } = [];
    }
}
