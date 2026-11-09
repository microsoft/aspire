// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Manages CancellationTokenSource instances for cross-process cancellation.
/// When a callback with a CancellationToken parameter is invoked, the host creates
/// a CancellationTokenSource and passes a token ID to the guest. The guest can
/// then cancel the token by calling the cancelToken RPC method.
/// </summary>
internal sealed class CancellationTokenRegistry : IDisposable
{
    // Early cancellation should normally be consumed as soon as the concurrently dispatched
    // invocation unmarshals its arguments. Bound markers that never find a matching invocation
    // so malformed or abandoned requests cannot retain memory for the connection lifetime.
    private const int MaxPendingCancellations = 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _sources = [];
    private readonly Dictionary<string, LinkedListNode<string>> _pendingCancellations = [];
    private readonly LinkedList<string> _pendingCancellationOrder = [];
    private int _counter;
    private bool _disposed;

    /// <summary>
    /// Creates a new CancellationTokenSource and returns its ID and token.
    /// The ID can be passed to the guest, which can use it to cancel the token.
    /// </summary>
    /// <returns>A tuple of (tokenId, cancellationToken).</returns>
    public (string TokenId, CancellationToken Token) Create()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var id = $"ct_{++_counter}";
            var cts = new CancellationTokenSource();
            _sources.Add(id, cts);
            return (id, cts.Token);
        }
    }

    /// <summary>
    /// Creates a new CancellationTokenSource linked to an existing token.
    /// This is useful when you need to combine multiple cancellation sources.
    /// </summary>
    /// <param name="linkedToken">The token to link to.</param>
    /// <returns>A tuple of (tokenId, cancellationToken).</returns>
    public (string TokenId, CancellationToken Token) CreateLinked(CancellationToken linkedToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var id = $"ct_{++_counter}";
            var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
            _sources.Add(id, cts);
            return (id, cts.Token);
        }
    }

    /// <summary>
    /// Cancels a CancellationTokenSource by its ID.
    /// </summary>
    /// <param name="tokenId">The token ID returned from Create().</param>
    /// <returns>True if the token was found and cancelled, false if not found.</returns>
    public bool Cancel(string tokenId)
    {
        CancellationTokenSource? source;
        lock (_lock)
        {
            if (!_sources.TryGetValue(tokenId, out source))
            {
                // JsonRpc dispatches requests concurrently, so cancelToken can run before
                // invokeCapability has unmarshalled and registered this guest-provided ID.
                if (!_pendingCancellations.ContainsKey(tokenId))
                {
                    var node = _pendingCancellationOrder.AddLast(tokenId);
                    _pendingCancellations.Add(tokenId, node);
                    if (_pendingCancellations.Count > MaxPendingCancellations)
                    {
                        var oldest = _pendingCancellationOrder.First!;
                        _pendingCancellationOrder.RemoveFirst();
                        _pendingCancellations.Remove(oldest.Value);
                    }
                }
                return false;
            }
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Unregister can dispose the source after the lookup.
            return false;
        }
    }

    /// <summary>
    /// Unregisters and disposes a CancellationTokenSource by its ID.
    /// Call this when the callback completes to clean up resources.
    /// </summary>
    /// <param name="tokenId">The token ID to unregister.</param>
    /// <returns>True if the token was found and unregistered, false if not found.</returns>
    public bool Unregister(string tokenId)
    {
        lock (_lock)
        {
            RemovePendingCancellation(tokenId);
            if (_sources.Remove(tokenId, out var cts))
            {
                cts.Dispose();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets the CancellationToken for a given ID.
    /// </summary>
    /// <param name="tokenId">The token ID.</param>
    /// <param name="token">The cancellation token if found.</param>
    /// <returns>True if found, false otherwise.</returns>
    public bool TryGetToken(string tokenId, out CancellationToken token)
    {
        lock (_lock)
        {
            if (_sources.TryGetValue(tokenId, out var cts))
            {
                token = cts.Token;
                return true;
            }

            token = default;
            return false;
        }
    }

    /// <summary>
    /// Gets or creates a CancellationTokenSource for a given guest-provided ID.
    /// If the ID already exists, returns the existing token.
    /// If the ID doesn't exist, creates a new CancellationTokenSource.
    /// </summary>
    /// <param name="tokenId">The token ID from the guest.</param>
    /// <returns>The CancellationToken for this ID.</returns>
    public CancellationToken GetOrCreate(string tokenId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_sources.TryGetValue(tokenId, out var existing))
            {
                return existing.Token;
            }

            var cts = new CancellationTokenSource();
            _sources.Add(tokenId, cts);
            if (RemovePendingCancellation(tokenId))
            {
                cts.Cancel();
            }

            return cts.Token;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var source in _sources.Values)
            {
                source.Dispose();
            }

            _sources.Clear();
            _pendingCancellations.Clear();
            _pendingCancellationOrder.Clear();
        }
    }

    private bool RemovePendingCancellation(string tokenId)
    {
        if (!_pendingCancellations.Remove(tokenId, out var node))
        {
            return false;
        }

        _pendingCancellationOrder.Remove(node);
        return true;
    }
}
