// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(ApplicationResourceCollectionDebugView))]
internal sealed class ResourceCollection : IResourceCollection
{
    private readonly List<IResource> _resources = [];
    private readonly Dictionary<string, IResource> _resourcesByName = new(StringComparers.ResourceName);

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
        get => _resources[index];
        set
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
        }
    }

    public int Count => _resources.Count;
    public bool IsReadOnly => false;

    public void Add(IResource item)
    {
        if (!_resourcesByName.TryAdd(item.Name, item))
        {
            ThrowDuplicateResource(item, _resourcesByName[item.Name]);
        }

        _resources.Add(item);
    }

    public void Clear()
    {
        _resources.Clear();
        _resourcesByName.Clear();
    }

    public bool Contains(IResource item) => _resources.Contains(item);
    public void CopyTo(IResource[] array, int arrayIndex) => _resources.CopyTo(array, arrayIndex);
    public IEnumerator<IResource> GetEnumerator() => _resources.GetEnumerator();
    public int IndexOf(IResource item) => _resources.IndexOf(item);

    public void Insert(int index, IResource item)
    {
        if (!_resourcesByName.TryAdd(item.Name, item))
        {
            ThrowDuplicateResource(item, _resourcesByName[item.Name]);
        }

        _resources.Insert(index, item);
    }

    public bool Remove(IResource item)
    {
        if (_resources.Remove(item))
        {
            _resourcesByName.Remove(item.Name);
            return true;
        }

        return false;
    }

    public void RemoveAt(int index)
    {
        var item = _resources[index];
        _resources.RemoveAt(index);
        _resourcesByName.Remove(item.Name);
    }

    public bool TryGetByName(string name, [NotNullWhen(true)] out IResource? resource)
    {
        if (name is null)
        {
            resource = null;
            return false;
        }

        return _resourcesByName.TryGetValue(name, out resource);
    }

    IEnumerator IEnumerable.GetEnumerator() => _resources.GetEnumerator();

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

