// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Immutable;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a collection of resource metadata annotations.
/// </summary>
/// <remarks>
/// This collection is thread-safe for concurrent reads and writes. Enumeration (including LINQ
/// methods like <c>OfType</c>, <c>Any</c>, <c>ToArray</c>, etc.) operates on a point-in-time
/// snapshot and is safe from concurrent modification exceptions.
/// </remarks>
public sealed class ResourceAnnotationCollection : IList<IResourceAnnotation>, IReadOnlyList<IResourceAnnotation>, IList
{
    // Using ImmutableArray<T> provides lock-free reads and snapshot semantics without per-enumeration
    // allocations. Writes create a new array (O(n)), but reads are very cheap (no locking, no copying).
    // This is ideal for Aspire's use case where reads (LINQ queries) vastly outnumber writes (Add during setup).
    private ImmutableArray<IResourceAnnotation> _items = [];
    private readonly object _writeLock = new();

    /// <summary>
    /// Gets the number of elements in the collection.
    /// </summary>
    public int Count => _items.Length;

    /// <summary>
    /// Gets or sets the element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element to get or set.</param>
    /// <returns>The element at the specified index.</returns>
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

    /// <summary>
    /// Adds an item to the collection.
    /// </summary>
    /// <param name="item">The object to add to the collection.</param>
    public void Add(IResourceAnnotation item)
    {
        lock (_writeLock)
        {
            _items = _items.Add(item);
        }
    }

    /// <summary>
    /// Removes all items from the collection.
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _items = [];
        }
    }

    /// <summary>
    /// Determines whether the collection contains a specific value.
    /// </summary>
    /// <param name="item">The object to locate in the collection.</param>
    /// <returns><see langword="true"/> if item is found in the collection; otherwise, <see langword="false"/>.</returns>
    public bool Contains(IResourceAnnotation item) => _items.Contains(item);

    /// <summary>
    /// Copies the elements of the collection to an array, starting at a particular array index.
    /// </summary>
    /// <param name="array">The one-dimensional array that is the destination of the elements copied from the collection.</param>
    /// <param name="arrayIndex">The zero-based index in array at which copying begins.</param>
    public void CopyTo(IResourceAnnotation[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>
    /// Determines the index of a specific item in the collection.
    /// </summary>
    /// <param name="item">The object to locate in the collection.</param>
    /// <returns>The index of item if found in the list; otherwise, -1.</returns>
    public int IndexOf(IResourceAnnotation item) => _items.IndexOf(item);

    /// <summary>
    /// Inserts an item to the collection at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index at which item should be inserted.</param>
    /// <param name="item">The object to insert into the collection.</param>
    public void Insert(int index, IResourceAnnotation item)
    {
        lock (_writeLock)
        {
            _items = _items.Insert(index, item);
        }
    }

    /// <summary>
    /// Removes the first occurrence of a specific object from the collection.
    /// </summary>
    /// <param name="item">The object to remove from the collection.</param>
    /// <returns><see langword="true"/> if item was successfully removed from the collection; otherwise, <see langword="false"/>.</returns>
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

    /// <summary>
    /// Removes the item at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the item to remove.</param>
    public void RemoveAt(int index)
    {
        lock (_writeLock)
        {
            _items = _items.RemoveAt(index);
        }
    }

    /// <summary>
    /// Copies the elements of the collection to a new array.
    /// </summary>
    /// <returns>An array containing copies of the elements of the collection.</returns>
    /// <remarks>
    /// This method provides an atomic snapshot copy, safe for use during concurrent modifications.
    /// </remarks>
    public IResourceAnnotation[] ToArray() => [.. _items];

    /// <summary>
    /// Returns an enumerator over a snapshot of the collection, safe for concurrent modification.
    /// </summary>
    /// <returns>An enumerator that can be used to iterate through the collection.</returns>
    /// <remarks>
    /// The enumerator iterates over a point-in-time snapshot of the collection. Modifications
    /// to the collection after the enumerator is obtained will not be reflected in the enumeration.
    /// This operation does not allocate; the enumerator is a value type.
    /// </remarks>
    public ImmutableArray<IResourceAnnotation>.Enumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<IResourceAnnotation> IEnumerable<IResourceAnnotation>.GetEnumerator() =>
        ((IEnumerable<IResourceAnnotation>)_items).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() =>
        ((IEnumerable<IResourceAnnotation>)_items).GetEnumerator();

    /// <inheritdoc/>
    bool ICollection<IResourceAnnotation>.IsReadOnly => false;

    #region IList (non-generic) explicit implementation

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => false;

    bool ICollection.IsSynchronized => true;

    object ICollection.SyncRoot => _writeLock;

    object? IList.this[int index]
    {
        get => this[index];
        set => this[index] = (IResourceAnnotation)value!;
    }

    int IList.Add(object? value)
    {
        lock (_writeLock)
        {
            var index = _items.Length;
            _items = _items.Add((IResourceAnnotation)value!);
            return index;
        }
    }

    bool IList.Contains(object? value) =>
        value is IResourceAnnotation annotation && Contains(annotation);

    int IList.IndexOf(object? value) =>
        value is IResourceAnnotation annotation ? IndexOf(annotation) : -1;

    void IList.Insert(int index, object? value) =>
        Insert(index, (IResourceAnnotation)value!);

    void IList.Remove(object? value)
    {
        if (value is IResourceAnnotation annotation)
        {
            Remove(annotation);
        }
    }

    void ICollection.CopyTo(Array array, int index) =>
        ((ICollection)_items).CopyTo(array, index);

    #endregion
}
