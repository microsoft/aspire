// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a collection of resource metadata annotations.
/// </summary>
public sealed class ResourceAnnotationCollection : Collection<IResourceAnnotation>
{
    private readonly object _lock = new();

    /// <inheritdoc/>
    protected override void InsertItem(int index, IResourceAnnotation item)
    {
        lock (_lock)
        {
            base.InsertItem(index, item);
        }
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, IResourceAnnotation item)
    {
        lock (_lock)
        {
            base.SetItem(index, item);
        }
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        lock (_lock)
        {
            base.RemoveItem(index);
        }
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        lock (_lock)
        {
            base.ClearItems();
        }
    }

    /// <summary>
    /// Returns an enumerator over a snapshot of the collection, safe for concurrent modification.
    /// </summary>
    public new IEnumerator<IResourceAnnotation> GetEnumerator()
    {
        IResourceAnnotation[] snapshot;
        lock (_lock)
        {
            snapshot = [.. Items];
        }
        return ((IEnumerable<IResourceAnnotation>)snapshot).GetEnumerator();
    }
}
