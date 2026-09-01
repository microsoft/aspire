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

    internal void MaterializeInheritedAnnotations<TAnnotation>(
        Func<TAnnotation, TAnnotation> clone,
        Func<TAnnotation, string>? keySelector = null)
        where TAnnotation : IResourceAnnotation
    {
        ArgumentNullException.ThrowIfNull(clone);

        if (Items is LayeredAnnotationList annotations)
        {
            annotations.MaterializeInheritedAnnotations(
                annotation => clone((TAnnotation)annotation),
                typeof(TAnnotation),
                keySelector is null ? null : annotation => keySelector((TAnnotation)annotation));
        }
    }

    internal void SuppressInheritedAnnotations<TAnnotation>()
        where TAnnotation : IResourceAnnotation
    {
        if (Items is LayeredAnnotationList annotations)
        {
            annotations.SuppressInheritedAnnotations(typeof(TAnnotation));
        }
    }

    internal void ConfigureProjection(Action configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (Items is not LayeredAnnotationList annotations)
        {
            configure();
            return;
        }

        annotations.HideInheritedAnnotations();
        try
        {
            configure();
        }
        finally
        {
            annotations.ShowInheritedAnnotations();
        }
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

    private interface IResourceAnnotationList : IList<IResourceAnnotation>
    {
        void SafeInsert(int index, IResourceAnnotation item);

        void SafeRemoveAt(int index);

        void SafeSetItem(int index, IResourceAnnotation item);
    }

    /// <summary>
    /// Thread-safe <see cref="IList{T}"/> backed by an <see cref="ImmutableArray{T}"/>.
    /// Reads are lock-free (they read the current immutable snapshot). Writes lock to swap
    /// the snapshot atomically.
    /// </summary>
    private sealed class ThreadSafeAnnotationList : IResourceAnnotationList
    {
        // Using ImmutableArray<T> provides lock-free reads and snapshot semantics without
        // per-enumeration allocations. Writes create a new array (O(n)), but reads are very
        // cheap (no locking, no copying). This is ideal for Aspire's use case where reads
        // (LINQ queries) vastly outnumber writes (Add during setup).
        private ImmutableArray<IResourceAnnotation> _items = [];
        private readonly object _writeLock = new();
        private readonly Dictionary<int, (int Index, IResourceAnnotation Item)> _indexedItemsByThread = [];

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

        public int IndexOf(IResourceAnnotation item)
        {
            var index = _items.IndexOf(item);
            if (index >= 0)
            {
                lock (_writeLock)
                {
                    _indexedItemsByThread[Environment.CurrentManagedThreadId] = (index, item);
                }
            }

            return index;
        }

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
                if (_indexedItemsByThread.Remove(Environment.CurrentManagedThreadId, out var indexedItem) &&
                    indexedItem.Index == index)
                {
                    var currentIndex = _items.IndexOf(indexedItem.Item);
                    if (currentIndex >= 0)
                    {
                        _items = _items.RemoveAt(currentIndex);
                    }

                    return;
                }

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
        private readonly HashSet<Type> _suppressedInheritedTypes = [];
        private readonly Dictionary<Type, KeyedAnnotationSuppression> _suppressedInheritedKeys = [];
        private readonly Dictionary<int, (int Index, IResourceAnnotation Item)> _indexedItemsByThread = [];
        private readonly object _writeLock = new();
        private List<IResourceAnnotation>? _detachedItems;
        private int? _hiddenInheritedAnnotationsThreadId;
        private int _hiddenInheritedAnnotationsDepth;

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
                SafeInsertCore(GetSnapshot().Length, item);
            }
        }

        public void Clear()
        {
            lock (_writeLock)
            {
                _detachedItems = [];
                _localItems.Clear();
                _removedInheritedItems.Clear();
                _suppressedInheritedTypes.Clear();
                _suppressedInheritedKeys.Clear();
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
                var snapshot = GetSnapshot();
                var index = snapshot.IndexOf(item);
                if (index >= 0)
                {
                    _indexedItemsByThread[Environment.CurrentManagedThreadId] = (index, snapshot[index]);
                }

                return index;
            }
        }

        public void Insert(int index, IResourceAnnotation item) => SafeInsert(index, item);

        public bool Remove(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                var snapshot = GetSnapshot();
                var index = snapshot.IndexOf(item);
                if (index < 0)
                {
                    return false;
                }

                RemoveItem(snapshot[index]);
                return true;
            }
        }

        public void RemoveAt(int index) => SafeRemoveAt(index);

        public void SafeInsert(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                SafeInsertCore(index, item);
            }
        }

        public void SafeRemoveAt(int index)
        {
            lock (_writeLock)
            {
                IResourceAnnotation? target = null;
                if (_indexedItemsByThread.Remove(Environment.CurrentManagedThreadId, out var indexedItem) &&
                    indexedItem.Index == index)
                {
                    target = indexedItem.Item;
                }

                var snapshot = GetSnapshot();
                if (target is null && (uint)index < (uint)snapshot.Length)
                {
                    target = snapshot[index];
                }

                if (target is not null)
                {
                    RemoveItem(target);
                }
            }
        }

        public void SafeSetItem(int index, IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                var snapshot = GetSnapshot();
                if ((uint)index >= (uint)snapshot.Length)
                {
                    return;
                }

                Detach(snapshot)[index] = item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void HideInheritedAnnotations()
        {
            lock (_writeLock)
            {
                var threadId = Environment.CurrentManagedThreadId;
                if (_hiddenInheritedAnnotationsThreadId is not null &&
                    _hiddenInheritedAnnotationsThreadId != threadId)
                {
                    throw new InvalidOperationException("Projection configuration is already running on another thread.");
                }

                _hiddenInheritedAnnotationsThreadId = threadId;
                _hiddenInheritedAnnotationsDepth++;
            }
        }

        public void ShowInheritedAnnotations()
        {
            lock (_writeLock)
            {
                if (_hiddenInheritedAnnotationsThreadId != Environment.CurrentManagedThreadId ||
                    _hiddenInheritedAnnotationsDepth == 0)
                {
                    throw new InvalidOperationException("Projection configuration scope is not active on this thread.");
                }

                _hiddenInheritedAnnotationsDepth--;
                if (_hiddenInheritedAnnotationsDepth == 0)
                {
                    _hiddenInheritedAnnotationsThreadId = null;
                }
            }
        }

        public void MaterializeInheritedAnnotations(
            Func<IResourceAnnotation, IResourceAnnotation> clone,
            Type annotationType,
            Func<IResourceAnnotation, string>? keySelector)
        {
            lock (_writeLock)
            {
                if (_detachedItems is not null)
                {
                    for (var i = 0; i < _detachedItems.Count; i++)
                    {
                        if (annotationType.IsInstanceOfType(_detachedItems[i]))
                        {
                            _detachedItems[i] = clone(_detachedItems[i]);
                        }
                    }

                    return;
                }

                var inherited = inheritedAnnotations
                    .Where(annotation =>
                        annotationType.IsInstanceOfType(annotation) &&
                        IsInheritedAnnotationVisible(annotation))
                    .ToArray();

                if (keySelector is null)
                {
                    _suppressedInheritedTypes.Add(annotationType);
                }
                else
                {
                    if (!_suppressedInheritedKeys.TryGetValue(annotationType, out var suppression))
                    {
                        suppression = new KeyedAnnotationSuppression(keySelector);
                        _suppressedInheritedKeys.Add(annotationType, suppression);
                    }

                    foreach (var annotation in inherited)
                    {
                        suppression.Keys.Add(keySelector(annotation));
                    }
                }

                _localItems.AddRange(inherited.Select(clone));
            }
        }

        public void SuppressInheritedAnnotations(Type annotationType)
        {
            lock (_writeLock)
            {
                if (_detachedItems is null)
                {
                    _suppressedInheritedTypes.Add(annotationType);
                }
            }
        }

        private void SafeInsertCore(int index, IResourceAnnotation item)
        {
            var snapshot = GetSnapshot();
            index = Math.Clamp(index, 0, snapshot.Length);

            if (_detachedItems is not null)
            {
                _detachedItems.Insert(index, item);
            }
            else if (index == snapshot.Length)
            {
                SuppressMatchingInheritedKey(item);
                _localItems.Add(item);
            }
            else
            {
                Detach(snapshot).Insert(index, item);
            }
        }

        private void RemoveItem(IResourceAnnotation item)
        {
            if (_detachedItems is not null)
            {
                var index = _detachedItems.FindIndex(candidate => ReferenceEquals(candidate, item));
                if (index >= 0)
                {
                    _detachedItems.RemoveAt(index);
                }

                return;
            }

            var localIndex = _localItems.FindIndex(candidate => ReferenceEquals(candidate, item));
            if (localIndex >= 0)
            {
                _localItems.RemoveAt(localIndex);
            }
            else if (inheritedAnnotations.Any(candidate => ReferenceEquals(candidate, item)))
            {
                _removedInheritedItems.Add(item);
            }
        }

        private List<IResourceAnnotation> Detach(ImmutableArray<IResourceAnnotation> snapshot)
        {
            _detachedItems ??= [.. snapshot];
            _localItems.Clear();
            _removedInheritedItems.Clear();
            _suppressedInheritedTypes.Clear();
            _suppressedInheritedKeys.Clear();

            return _detachedItems;
        }

        private ImmutableArray<IResourceAnnotation> GetSnapshot()
        {
            if (_detachedItems is not null)
            {
                return [.. _detachedItems];
            }

            var hideInherited = _hiddenInheritedAnnotationsThreadId == Environment.CurrentManagedThreadId &&
                _hiddenInheritedAnnotationsDepth > 0;

            return hideInherited
                ? [.. _localItems]
                : [.. inheritedAnnotations.Where(IsInheritedAnnotationVisible), .. _localItems];
        }

        private bool IsInheritedAnnotationVisible(IResourceAnnotation annotation)
        {
            return !_removedInheritedItems.Contains(annotation) &&
                !_suppressedInheritedTypes.Any(type => type.IsAssignableFrom(annotation.GetType())) &&
                !_suppressedInheritedKeys.Any(pair =>
                    pair.Key.IsAssignableFrom(annotation.GetType()) &&
                    pair.Value.Keys.Contains(pair.Value.KeySelector(annotation)));
        }

        private void SuppressMatchingInheritedKey(IResourceAnnotation annotation)
        {
            foreach (var pair in _suppressedInheritedKeys)
            {
                if (pair.Key.IsAssignableFrom(annotation.GetType()))
                {
                    pair.Value.Keys.Add(pair.Value.KeySelector(annotation));
                }
            }
        }

        private sealed class KeyedAnnotationSuppression(Func<IResourceAnnotation, string> keySelector)
        {
            public Func<IResourceAnnotation, string> KeySelector { get; } = keySelector;

            public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
