// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AspireWithBlazor.ClientServiceDefaults.Telemetry;

/// <summary>
/// A simple circular buffer for storing telemetry items before export.
/// Thread-safe for single-threaded WebAssembly environment.
/// </summary>
/// <typeparam name="T">The type of items to store.</typeparam>
internal sealed class CircularBuffer<T>
{
    private readonly T?[] _buffer;
    private readonly int _capacity;
    private readonly object _lock = new();
    private int _head;
    private int _tail;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircularBuffer{T}"/> class.
    /// </summary>
    /// <param name="capacity">The maximum capacity of the buffer.</param>
    public CircularBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new T?[capacity];
        _head = 0;
        _tail = 0;
        _count = 0;
    }

    /// <summary>
    /// Gets the current number of items in the buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Tries to add an item to the buffer.
    /// </summary>
    /// <param name="item">The item to add.</param>
    /// <returns>True if the item was added; false if the buffer is full.</returns>
    public bool TryAdd(T item)
    {
        lock (_lock)
        {
            if (_count >= _capacity)
            {
                return false;
            }

            _buffer[_tail] = item;
            _tail = (_tail + 1) % _capacity;
            _count++;
            return true;
        }
    }

    /// <summary>
    /// Tries to take an item from the buffer.
    /// </summary>
    /// <param name="item">The retrieved item.</param>
    /// <returns>True if an item was retrieved; false if the buffer is empty.</returns>
    public bool TryTake(out T? item)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                item = default;
                return false;
            }

            item = _buffer[_head];
            _buffer[_head] = default;
            _head = (_head + 1) % _capacity;
            _count--;
            return true;
        }
    }

    /// <summary>
    /// Clears all items from the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _capacity);
            _head = 0;
            _tail = 0;
            _count = 0;
        }
    }
}
