// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The default <see cref="IResourceCollection"/> implementation backing
/// <see cref="DistributedApplicationModel.Resources"/>. Maintains a name index alongside the
/// ordered resource list and supports reads that are concurrent with mutations.
/// </summary>
/// <remarks>
/// This collection is safe to read (enumerate, index, look up by name) while it is being mutated
/// from another thread. That matters because <see cref="DistributedApplicationModel.Resources"/> is a
/// single shared instance handed to every pipeline step, and <c>DistributedApplicationPipeline</c>
/// runs independent steps concurrently (see <c>ExecuteStepsAsTaskDag</c>). Steps that only declare a
/// <c>RequiredBySteps</c> relationship to a common aggregation step — for example
/// <c>azure-prepare-resources</c>, which appends role-assignment and identity resources, and
/// <c>validate-compute-environments</c>, which enumerates the model — have no ordering relative to
/// one another and therefore genuinely overlap. Backing the reads with a plain <see cref="List{T}"/>
/// meant an append on one step invalidated an in-flight enumerator on another
/// ("Collection was modified; enumeration operation may not execute"), and two concurrent appends
/// could corrupt the list and the name index. See https://github.com/microsoft/aspire/issues/19266.
/// <para>
/// Reads are served from an immutable copy-on-write snapshot rather than the live list, so a reader
/// observes a stable point-in-time view for the duration of its enumeration and never sees a torn
/// state. The snapshot is built lazily and cached, so a burst of mutations (the common case, during
/// application build) costs a single rebuild on the next read instead of one per mutation. Writes
/// take the lock, which also protects the name index and the backing list from concurrent writers.
/// </para>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(ApplicationResourceCollectionDebugView))]
internal sealed class ResourceCollection : IResourceCollection
{
    private readonly List<IResource> _resources = [];
    private readonly Dictionary<string, IResource> _resourcesByName = new(StringComparers.ResourceName);
    private readonly object _lock = new();

    // Cached copy-on-write view of _resources, or null when a mutation has invalidated it.
    // Only ever published from inside _lock so it always matches _resources at publication time.
    private IResource[]? _snapshot;

    public ResourceCollection() { }

    public ResourceCollection(IEnumerable<IResource> resources)
    {
        foreach (var resource in resources)
        {
            if (!_resourcesByName.TryAdd(resource.Name, resource))
            {
                ThrowDuplicateResource(resource, _resourcesByName[resource.Name]);
            }

            _resources.Add(resource);
        }
    }

    public IResource this[int index]
    {
        get => GetSnapshot()[index];
        set
        {
            lock (_lock)
            {
                var old = _resources[index];

                // Allow replacing with same name (same slot), but reject if a *different* slot already has this name.
                if (!StringComparers.ResourceName.Equals(old.Name, value.Name) &&
                    _resourcesByName.TryGetValue(value.Name, out var existing))
                {
                    ThrowDuplicateResource(value, existing);
                }

                _resources[index] = value;
                _resourcesByName.Remove(old.Name);
                _resourcesByName[value.Name] = value;
                _snapshot = null;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _resources.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(IResource item)
    {
        lock (_lock)
        {
            if (!_resourcesByName.TryAdd(item.Name, item))
            {
                ThrowDuplicateResource(item, _resourcesByName[item.Name]);
            }

            _resources.Add(item);
            _snapshot = null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _resources.Clear();
            _resourcesByName.Clear();
            _snapshot = null;
        }
    }

    public bool Contains(IResource item)
    {
        lock (_lock)
        {
            return _resources.Contains(item);
        }
    }

    public void CopyTo(IResource[] array, int arrayIndex)
    {
        lock (_lock)
        {
            _resources.CopyTo(array, arrayIndex);
        }
    }

    public IEnumerator<IResource> GetEnumerator() => ((IEnumerable<IResource>)GetSnapshot()).GetEnumerator();

    public int IndexOf(IResource item)
    {
        lock (_lock)
        {
            return _resources.IndexOf(item);
        }
    }

    public void Insert(int index, IResource item)
    {
        lock (_lock)
        {
            if (!_resourcesByName.TryAdd(item.Name, item))
            {
                ThrowDuplicateResource(item, _resourcesByName[item.Name]);
            }

            _resources.Insert(index, item);
            _snapshot = null;
        }
    }

    public bool Remove(IResource item)
    {
        lock (_lock)
        {
            if (_resources.Remove(item))
            {
                _resourcesByName.Remove(item.Name);
                _snapshot = null;
                return true;
            }

            return false;
        }
    }

    public void RemoveAt(int index)
    {
        lock (_lock)
        {
            var item = _resources[index];
            _resources.RemoveAt(index);
            _resourcesByName.Remove(item.Name);
            _snapshot = null;
        }
    }

    public bool TryGetByName(string name, [NotNullWhen(true)] out IResource? resource)
    {
        if (name is null)
        {
            resource = null;
            return false;
        }

        lock (_lock)
        {
            return _resourcesByName.TryGetValue(name, out resource);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IResource[] GetSnapshot()
    {
        // Lock-free fast path. A snapshot that a concurrent mutation invalidates immediately after
        // this read is still a valid point-in-time view, which is exactly what a reader wants.
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_lock)
        {
            // Another reader may have published a snapshot while this one waited for the lock.
            snapshot = _snapshot;
            if (snapshot is null)
            {
                snapshot = _resources.ToArray();
                Volatile.Write(ref _snapshot, snapshot);
            }

            return snapshot;
        }
    }

    [DoesNotReturn]
    private static void ThrowDuplicateResource(IResource newResource, IResource existingResource)
    {
        throw new DistributedApplicationException($"Cannot add resource of type '{newResource.GetType()}' with name '{newResource.Name}' because resource of type '{existingResource.GetType()}' with that name already exists. Resource names are case-insensitive.");
    }

    private sealed class ApplicationResourceCollectionDebugView(ResourceCollection collection)
    {
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public ResourceDebugView[] Items => [.. collection.Select(x => new ResourceDebugView { Resource = x })];

        [DebuggerDisplay("{Resource}", Name = "{Resource.Name}")]
        public sealed class ResourceDebugView
        {
            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public required IResource Resource { get; init; }
        }
    }
}

