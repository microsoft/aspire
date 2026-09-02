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

    internal void RemoveAnnotations<TAnnotation>()
        where TAnnotation : IResourceAnnotation
    {
        foreach (var annotation in this.OfType<TAnnotation>().ToArray())
        {
            Remove(annotation);
        }
    }

    /// <summary>
    /// Monotonically increasing mutation counter used by layered collections to invalidate
    /// their cached merged snapshot when this collection changes.
    /// </summary>
    internal int Version => ((IResourceAnnotationList)Items).Version;

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
        /// <summary>
        /// Monotonically increasing counter bumped on every mutation. A layered collection uses
        /// the owner's version to know when its cached merged snapshot is stale.
        /// </summary>
        int Version { get; }

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
        private int _version;

        public int Count => _items.Length;

        public int Version => Volatile.Read(ref _version);

        public bool IsReadOnly => false;

        public IResourceAnnotation this[int index]
        {
            get => _items[index];
            set
            {
                lock (_writeLock)
                {
                    _items = _items.SetItem(index, value);
                    _version++;
                }
            }
        }

        public void Add(IResourceAnnotation item)
        {
            lock (_writeLock)
            {
                _items = _items.Add(item);
                _version++;
            }
        }

        public void Clear()
        {
            lock (_writeLock)
            {
                _items = [];
                _version++;
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
                _version++;
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
                _version++;
            }
        }

        /// <summary>
        /// Removes the item at the given index, skipping the operation if the index is
        /// out of range under the lock (stale index from Collection&lt;T&gt;.Remove).
        /// </summary>
        public void SafeRemoveAt(int index)
        {
            // Only an out-of-range index can be corrected here. Collection<T>.Remove(item) computes
            // IndexOf outside the lock and then calls RemoveItem(index), so a concurrent insert can
            // still shift the target. Resolving by item identity instead is not an option: RemoveItem
            // cannot distinguish that case from a deliberate RemoveAt(index) whose index intentionally
            // refers to a different element, and guessing corrupts the collection in the common
            // single-threaded case. Callers that need atomic remove-by-identity must serialize.
            lock (_writeLock)
            {
                if ((uint)index < (uint)_items.Length)
                {
                    _items = _items.RemoveAt(index);
                    _version++;
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
                    _version++;
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
                _version++;
                return true;
            }
        }

        public void RemoveAt(int index)
        {
            lock (_writeLock)
            {
                _items = _items.RemoveAt(index);
                _version++;
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
        private readonly object _writeLock = new();
        private List<IResourceAnnotation>? _detachedItems;
        private int? _hiddenInheritedAnnotationsThreadId;
        private int _hiddenInheritedAnnotationsDepth;
        private int _version;

        // Reads rebuild the merged owner + local view, which is hot enough (every LINQ query over
        // Resource.Annotations) that recomputing it per read is measurable. Cache the merged
        // snapshot and invalidate whenever this layer or the owner layer changes.
        private ImmutableArray<IResourceAnnotation> _cachedSnapshot;
        private bool _hasCachedSnapshot;
        private int _cachedInheritedVersion = -1;

        public int Version => Volatile.Read(ref _version);

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
                Invalidate();
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
            // See ThreadSafeAnnotationList.SafeRemoveAt: only an out-of-range index can be
            // corrected here, because RemoveItem cannot tell a shifted Remove(item) apart from a
            // deliberate RemoveAt(index).
            lock (_writeLock)
            {
                var snapshot = GetSnapshot();
                if ((uint)index < (uint)snapshot.Length)
                {
                    RemoveItem(snapshot[index]);
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

                ThrowIfIndexedMutationDuringConfiguration();
                Detach(snapshot)[index] = item;
                Invalidate();
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
                // Never throw here. This runs in a finally block, so throwing would replace any
                // exception the configuration callback raised with an unrelated state error.
                if (_hiddenInheritedAnnotationsThreadId != Environment.CurrentManagedThreadId ||
                    _hiddenInheritedAnnotationsDepth == 0)
                {
                    return;
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

                    Invalidate();
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
                Invalidate();
            }
        }

        public void SuppressInheritedAnnotations(Type annotationType)
        {
            lock (_writeLock)
            {
                if (_detachedItems is null && _suppressedInheritedTypes.Add(annotationType))
                {
                    Invalidate();
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
                ThrowIfIndexedMutationDuringConfiguration();
                Detach(snapshot).Insert(index, item);
            }

            Invalidate();
        }

        // Inserting or replacing by index forces the layered view to collapse into a flat list,
        // which requires a snapshot that includes inherited annotations. Inside a configuration
        // callback the caller only sees the local layer, so collapsing there would silently drop
        // every owner annotation. Append and remove-by-identity stay layered and remain allowed.
        private void ThrowIfIndexedMutationDuringConfiguration()
        {
            if (AreInheritedAnnotationsHidden())
            {
                throw new InvalidOperationException(
                    "Annotations cannot be inserted or replaced by index while a projection configuration callback is running. Use Add or Remove instead.");
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
                    Invalidate();
                }

                return;
            }

            var localIndex = _localItems.FindIndex(candidate => ReferenceEquals(candidate, item));
            if (localIndex >= 0)
            {
                _localItems.RemoveAt(localIndex);
                Invalidate();
            }
            else if (ContainsInherited(item))
            {
                _removedInheritedItems.Add(item);
                Invalidate();
            }
        }

        // Enumerating the owner collection here is safe under _writeLock because the owner's
        // backing store performs lock-free reads, so no owner lock is acquired while this
        // projection's lock is held and the two layers cannot deadlock against each other.
        private bool ContainsInherited(IResourceAnnotation item)
        {
            foreach (var candidate in inheritedAnnotations)
            {
                if (ReferenceEquals(candidate, item))
                {
                    return true;
                }
            }

            return false;
        }

        private List<IResourceAnnotation> Detach(ImmutableArray<IResourceAnnotation> snapshot)
        {
            _detachedItems ??= [.. snapshot];
            _localItems.Clear();
            _removedInheritedItems.Clear();
            _suppressedInheritedTypes.Clear();
            _suppressedInheritedKeys.Clear();
            Invalidate();

            return _detachedItems;
        }

        private void Invalidate()
        {
            _version++;
            _hasCachedSnapshot = false;
        }

        private bool AreInheritedAnnotationsHidden() =>
            _hiddenInheritedAnnotationsDepth > 0 &&
            _hiddenInheritedAnnotationsThreadId == Environment.CurrentManagedThreadId;

        private ImmutableArray<IResourceAnnotation> GetSnapshot()
        {
            if (_detachedItems is not null)
            {
                if (!_hasCachedSnapshot)
                {
                    _cachedSnapshot = [.. _detachedItems];
                    _hasCachedSnapshot = true;
                }

                return _cachedSnapshot;
            }

            // The hidden view is thread dependent, so it must never populate the shared cache.
            if (AreInheritedAnnotationsHidden())
            {
                return [.. _localItems];
            }

            var inheritedVersion = inheritedAnnotations.Version;
            if (!_hasCachedSnapshot || _cachedInheritedVersion != inheritedVersion)
            {
                var builder = ImmutableArray.CreateBuilder<IResourceAnnotation>(_localItems.Count);
                foreach (var annotation in inheritedAnnotations)
                {
                    if (IsInheritedAnnotationVisible(annotation))
                    {
                        builder.Add(annotation);
                    }
                }

                builder.AddRange(_localItems);

                _cachedSnapshot = builder.ToImmutable();
                _hasCachedSnapshot = true;
                _cachedInheritedVersion = inheritedVersion;
            }

            return _cachedSnapshot;
        }

        private bool IsInheritedAnnotationVisible(IResourceAnnotation annotation)
        {
            if (_removedInheritedItems.Count == 0 &&
                _suppressedInheritedTypes.Count == 0 &&
                _suppressedInheritedKeys.Count == 0)
            {
                return true;
            }

            if (_removedInheritedItems.Contains(annotation))
            {
                return false;
            }

            var annotationType = annotation.GetType();

            foreach (var suppressedType in _suppressedInheritedTypes)
            {
                if (suppressedType.IsAssignableFrom(annotationType))
                {
                    return false;
                }
            }

            foreach (var pair in _suppressedInheritedKeys)
            {
                if (pair.Key.IsAssignableFrom(annotationType) &&
                    pair.Value.Keys.Contains(pair.Value.KeySelector(annotation)))
                {
                    return false;
                }
            }

            return true;
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
