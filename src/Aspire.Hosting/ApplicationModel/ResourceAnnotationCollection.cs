// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a collection of resource metadata annotations.
/// </summary>
// Inherits from Collection<T> to maintain binary compatibility with assemblies compiled against
// earlier Aspire versions. Externally compiled code references Collection<T>.Add() etc. via
// method tokens on the base class; removing the base class causes an ExecutionEngineException
// at the call site. Thread safety is provided by a custom IList<T> backing store that uses
// ImmutableArray<T> internally (lock-free reads, locked writes).
public sealed class ResourceAnnotationCollection : Collection<IResourceAnnotation>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceAnnotationCollection"/> class.
    /// </summary>
    public ResourceAnnotationCollection()
        : base(new ThreadSafeAnnotationList())
    {
    }

    /// <summary>
    /// Thread-safe <see cref="IList{T}"/> backed by an <see cref="ImmutableArray{T}"/>.
    /// Reads are lock-free (they read the current immutable snapshot). Writes lock to swap
    /// the snapshot atomically.
    /// </summary>
    private sealed class ThreadSafeAnnotationList : IList<IResourceAnnotation>
    {
        // Using ImmutableArray<T> provides lock-free reads and snapshot semantics without
        // per-enumeration allocations. Writes create a new array (O(n)), but reads are very
        // cheap (no locking, no copying). This is ideal for Aspire's use case where reads
        // (LINQ queries) vastly outnumber writes (Add during setup).
        private ImmutableArray<IResourceAnnotation> _items = [];
        private readonly object _writeLock = new();

        public int Count => _items.Length;

        public bool IsReadOnly => false;

        public IResourceAnnotation this[int index]
        {
            get => _items[index];
            set
            {
                lock (_writeLock)
                {
                    _items = _items.SetItem(index, value);
                }
            }
        }

        public void Add(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                _items = _items.Add(item);
            }
        }

        public void Clear()
        {
            lock (_writeLock)
            {
                _items = [];
            }
        }

        public bool Contains(IResourceAnnotation item) => _items.Contains(item);

        public void CopyTo(IResourceAnnotation[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public int IndexOf(IResourceAnnotation item) => _items.IndexOf(item);

        public void Insert(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                _items = _items.Insert(index, item);
            }
        }

        public bool Remove(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                var index = _items.IndexOf(item);
                if (index < 0)
                {
                    return false;
                }
                _items = _items.RemoveAt(index);
                return true;
            }
        }

        public void RemoveAt(int index)
        {
            lock (_writeLock)
            {
                _items = _items.RemoveAt(index);
            }
        }

        public IEnumerator<IResourceAnnotation> GetEnumerator() =>
            ((IEnumerable<IResourceAnnotation>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            ((IEnumerable<IResourceAnnotation>)_items).GetEnumerator();
    }
}
