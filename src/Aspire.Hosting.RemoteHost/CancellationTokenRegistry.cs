// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Manages CancellationTokenSource instances for cross-process cancellation.
/// When a callback with a CancellationToken parameter is invoked, the host creates
/// a CancellationTokenSource and passes a token ID to the guest. The guest can
/// then cancel the token by calling the cancelToken RPC method.
/// </summary>
internal sealed class CancellationTokenRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new();
    private int _counter;
    private bool _disposed;

    /// <summary>
    /// Creates a new CancellationTokenSource and returns its ID and token.
    /// The ID can be passed to the guest, which can use it to cancel the token.
    /// </summary>
    /// <returns>A tuple of (tokenId, cancellationToken).</returns>
    public (string TokenId, CancellationToken Token) Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = $"ct_{Interlocked.Increment(ref _counter)}";
        var cts = new CancellationTokenSource();

        if (!_sources.TryAdd(id, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Failed to register cancellation token with ID '{id}'");
        }

        return (id, cts.Token);
    }

    /// <summary>
    /// Creates a new CancellationTokenSource linked to an existing token.
    /// This is useful when you need to combine multiple cancellation sources.
    /// </summary>
    /// <param name="linkedToken">The token to link to.</param>
    /// <returns>A tuple of (tokenId, cancellationToken).</returns>
    public (string TokenId, CancellationToken Token) CreateLinked(CancellationToken linkedToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = $"ct_{Interlocked.Increment(ref _counter)}";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);

        if (!_sources.TryAdd(id, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Failed to register cancellation token with ID '{id}'");
        }

        return (id, cts.Token);
    }

    /// <summary>
    /// Cancels a CancellationTokenSource by its ID.
    /// </summary>
    /// <param name="tokenId">The token ID returned from Create().</param>
    /// <returns>True if the token was found and cancelled, false if not found.</returns>
    public bool Cancel(string tokenId)
    {
        if (_sources.TryGetValue(tokenId, out var cts))
        {
            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, that's fine
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Unregisters and disposes a CancellationTokenSource by its ID.
    /// Call this when the callback completes to clean up resources.
    /// </summary>
    /// <param name="tokenId">The token ID to unregister.</param>
    /// <returns>True if the token was found and unregistered, false if not found.</returns>
    public bool Unregister(string tokenId)
    {
        if (_sources.TryRemove(tokenId, out var cts))
        {
            cts.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets the CancellationToken for a given ID.
    /// </summary>
    /// <param name="tokenId">The token ID.</param>
    /// <param name="token">The cancellation token if found.</param>
    /// <returns>True if found, false otherwise.</returns>
    public bool TryGetToken(string tokenId, out CancellationToken token)
    {
        if (_sources.TryGetValue(tokenId, out var cts))
        {
            token = cts.Token;
            return true;
        }
        token = default;
        return false;
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Try to get existing first
        if (_sources.TryGetValue(tokenId, out var existing))
        {
            return existing.Token;
        }

        // Create a new one
        var cts = new CancellationTokenSource();

        // Use GetOrAdd to handle race conditions
        var added = _sources.GetOrAdd(tokenId, cts);
        if (added != cts)
        {
            // Another thread added first, dispose our copy
            cts.Dispose();
        }

        return added.Token;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose all registered CancellationTokenSources
        foreach (var kvp in _sources)
        {
            try
            {
                kvp.Value.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
        _sources.Clear();
    }
}
