// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides an asynchronously initialized value that:
/// - Can be awaited via GetValueAsync() until the first value or exception is set.
/// - Exposes the latest value after it has been set (supports re-setting).
/// - Tracks whether a value or exception was ever set via IsValueSet.
/// - Supports setting an exception that will be thrown by GetValueAsync.
/// 
/// Thread-safe for concurrent SetValue / SetException / GetValueAsync calls.
/// </summary>
[DebuggerDisplay("{Value= {DebuggerValue()}")]
public sealed class ValueSnapshot<T> where T : notnull
{
    private readonly TaskCompletionSource<T> _firstValueTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _lock = new();
    private Task<T>? _currentValue;

    /// <summary>
    /// True once a value or exception has been set at least once.
    /// </summary>
    public bool IsValueSet => _firstValueTcs.Task.IsCompleted;

    /// <summary>
    /// Await the current value:
    /// - If a value has already been set, returns it immediately.
    /// - If an exception has been set, throws it.
    /// - Otherwise waits until the first value or exception is set.
    /// Always returns the latest value at the moment of completion or throws the exception.
    /// </summary>
    public Task<T> GetValueAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Return updated value if it exists, otherwise return the first value task
            return _currentValue ?? _firstValueTcs.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Sets (or updates) the value. The first successful call completes any
    /// pending GetValueAsync waiters. Subsequent calls replace the current value.
    /// </summary>
    public void SetValue(T value)
    {
        lock (_lock)
        {
            if (!_firstValueTcs.TrySetResult(value))
            {
                _currentValue = Task.FromResult(value);
            }
        }
    }

    /// <summary>
    /// Sets an exception that will be thrown by GetValueAsync.
    /// The first successful call (either SetValue or SetException) completes any
    /// pending GetValueAsync waiters.
    /// </summary>
    public void SetException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_lock)
        {
            if (!_firstValueTcs.TrySetException(exception))
            {
                _currentValue = Task.FromException<T>(exception);
            }
        }
    }
    private T? DebuggerValue() => IsValueSet ? _firstValueTcs.Task.Result : default;
}
