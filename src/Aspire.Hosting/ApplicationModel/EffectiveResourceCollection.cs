// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

// Reads expose the selected shape so model consumers cannot silently overlook projections. Writes
// canonicalize back to owners because projections are views, never independent model members.
internal sealed class EffectiveResourceCollection(IResourceCollection owners) : IResourceCollection
{
    public IResource this[int index]
    {
        get => owners[index].GetEffectiveResource();
        set => owners[index] = value.GetOwnerOrSelf();
    }

    public int Count => owners.Count;

    public bool IsReadOnly => owners.IsReadOnly;

    public void Add(IResource item)
    {
        owners.Add(item.GetOwnerOrSelf());
    }

    public void Clear()
    {
        owners.Clear();
    }

    public bool Contains(IResource item)
    {
        return owners.Contains(item.GetOwnerOrSelf());
    }

    public void CopyTo(IResource[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);

        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(arrayIndex, array.Length);

        if (array.Length - arrayIndex < Count)
        {
            throw new ArgumentException("The destination array is not large enough.", nameof(array));
        }

        for (var i = 0; i < Count; i++)
        {
            array[arrayIndex + i] = this[i];
        }
    }

    public IEnumerator<IResource> GetEnumerator()
    {
        foreach (var owner in owners)
        {
            yield return owner.GetEffectiveResource();
        }
    }

    public int IndexOf(IResource item)
    {
        return owners.IndexOf(item.GetOwnerOrSelf());
    }

    public void Insert(int index, IResource item)
    {
        owners.Insert(index, item.GetOwnerOrSelf());
    }

    public bool Remove(IResource item)
    {
        return owners.Remove(item.GetOwnerOrSelf());
    }

    public void RemoveAt(int index)
    {
        owners.RemoveAt(index);
    }

    public bool TryGetByName(string name, [NotNullWhen(true)] out IResource? resource)
    {
        if (owners.TryGetByName(name, out var owner))
        {
            resource = owner.GetEffectiveResource();
            return true;
        }

        resource = null;
        return false;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
