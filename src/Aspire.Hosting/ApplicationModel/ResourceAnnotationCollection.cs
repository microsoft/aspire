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

    internal ResourceAnnotationCollection(ResourceAnnotationCollection inheritedAnnotations)
        : base(new LayeredAnnotationList(inheritedAnnotations))
    {
    }

    // Override Collection<T> virtual methods to perform mutations atomically on the backing
    // store. Collection<T>.Add/Remove read items.Count/items.IndexOf outside any lock, then
    // pass the (potentially stale) index to these virtuals. By overriding, we can clamp or
    // re-validate the index under the write lock to avoid ArgumentOutOfRangeException when
    // concurrent modifications shift indices between the read and the write.

    /// <inheritdoc/>
    protected override void InsertItem(int index, IResourceAnnotation item)
    {
        ((IResourceAnnotationList)Items).SafeInsert(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        ((IResourceAnnotationList)Items).SafeRemoveAt(index);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, IResourceAnnotation item)
    {
        ((IResourceAnnotationList)Items).SafeSetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        Items.Clear();
    }

    /// <summary>
    /// Thread-safe <see cref="IList{T}"/> backed by an <see cref="ImmutableArray{T}"/>.
    /// Reads are lock-free (they read the current immutable snapshot). Writes lock to swap
    /// the snapshot atomically.
    /// </summary>
    private interface IResourceAnnotationList : IList<IResourceAnnotation>
    {
        void SafeInsert(int index, IResourceAnnotation item);

        void SafeRemoveAt(int index);

        void SafeSetItem(int index, IResourceAnnotation item);
    }

    private sealed class ThreadSafeAnnotationList : IResourceAnnotationList
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

        /// <summary>
        /// Inserts an item at the given index, clamping to the valid range under the lock
        /// so a stale index from Collection&lt;T&gt;.Add does not throw.
        /// </summary>
        public void SafeInsert(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                index = Math.Clamp(index, 0, _items.Length);
                _items = _items.Insert(index, item);
            }
        }

        /// <summary>
        /// Removes the item at the given index, skipping the operation if the index is
        /// out of range under the lock (stale index from Collection&lt;T&gt;.Remove).
        /// </summary>
        public void SafeRemoveAt(int index)
        {
            lock (_writeLock)
            {
                if ((uint)index < (uint)_items.Length)
                {
                    _items = _items.RemoveAt(index);
                }
            }
        }

        /// <summary>
        /// Sets the item at the given index, skipping the operation if the index is
        /// out of range under the lock.
        /// </summary>
        public void SafeSetItem(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                if ((uint)index < (uint)_items.Length)
                {
                    _items = _items.SetItem(index, item);
                }
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

    /// <summary>
    /// Exposes owner annotations followed by projection-local annotations. Mutations are always
    /// applied to the local layer; removing an inherited annotation records a tombstone so
    /// replace-style builder APIs can override owner configuration without mutating the owner.
    /// </summary>
    private sealed class LayeredAnnotationList(ResourceAnnotationCollection inheritedAnnotations) : IResourceAnnotationList
    {
        private readonly List<IResourceAnnotation> _localItems = [];
        private readonly HashSet<IResourceAnnotation> _removedInheritedItems = new(ReferenceEqualityComparer.Instance);
        private readonly object _writeLock = new();
        private bool _clearInherited;

        public int Count
        {
            get
            {
                lock (_writeLock)
                {
                    return GetSnapshot().Length;
                }
            }
        }

        public bool IsReadOnly => false;

        public IResourceAnnotation this[int index]
        {
            get
            {
                lock (_writeLock)
                {
                    return GetSnapshot()[index];
                }
            }
            set => SafeSetItem(index, value);
        }

        public void Add(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                _localItems.Add(item);
            }
        }

        public void Clear()
        {
            lock (_writeLock)
            {
                foreach (var annotation in inheritedAnnotations)
                {
                    _removedInheritedItems.Add(annotation);
                }

                _clearInherited = true;
                _localItems.Clear();
            }
        }

        public bool Contains(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                return GetSnapshot().Contains(item);
            }
        }

        public void CopyTo(IResourceAnnotation[] array, int arrayIndex)
        {
            lock (_writeLock)
            {
                GetSnapshot().CopyTo(array, arrayIndex);
            }
        }

        public IEnumerator<IResourceAnnotation> GetEnumerator()
        {
            lock (_writeLock)
            {
                return ((IEnumerable<IResourceAnnotation>)GetSnapshot()).GetEnumerator();
            }
        }

        public int IndexOf(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                return GetSnapshot().IndexOf(item);
            }
        }

        public void Insert(int index, IResourceAnnotation item) => SafeInsert(index, item);

        public bool Remove(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                var localIndex = _localItems.IndexOf(item);
                if (localIndex >= 0)
                {
                    _localItems.RemoveAt(localIndex);
                    return true;
                }

                if (inheritedAnnotations.Contains(item) && !_removedInheritedItems.Contains(item))
                {
                    _removedInheritedItems.Add(item);
                    return true;
                }

                return false;
            }
        }

        public void RemoveAt(int index) => SafeRemoveAt(index);

        public void SafeInsert(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                var inheritedCount = GetInheritedSnapshot().Length;
                var localIndex = Math.Clamp(index - inheritedCount, 0, _localItems.Count);
                _localItems.Insert(localIndex, item);
            }
        }

        public void SafeRemoveAt(int index)
        {
            lock (_writeLock)
            {
                var inherited = GetInheritedSnapshot();
                if ((uint)index < (uint)inherited.Length)
                {
                    _removedInheritedItems.Add(inherited[index]);
                    return;
                }

                var localIndex = index - inherited.Length;
                if ((uint)localIndex < (uint)_localItems.Count)
                {
                    _localItems.RemoveAt(localIndex);
                }
            }
        }

        public void SafeSetItem(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                var inherited = GetInheritedSnapshot();
                if ((uint)index < (uint)inherited.Length)
                {
                    _removedInheritedItems.Add(inherited[index]);
                    _localItems.Insert(0, item);
                    return;
                }

                var localIndex = index - inherited.Length;
                if ((uint)localIndex < (uint)_localItems.Count)
                {
                    _localItems[localIndex] = item;
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private ImmutableArray<IResourceAnnotation> GetInheritedSnapshot()
        {
            if (_clearInherited)
            {
                return [];
            }

            return [.. inheritedAnnotations.Where(annotation => !_removedInheritedItems.Contains(annotation))];
        }

        private ImmutableArray<IResourceAnnotation> GetSnapshot()
        {
            return [.. GetInheritedSnapshot(), .. _localItems];
        }
    }
}
