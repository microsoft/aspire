// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Represents a reference to a streaming interaction asset. Returned once
/// the first chunk from the server has been received so the content type is known.
/// </summary>
public sealed class AssetReference : IDisposable
{
    private readonly Func<Stream, CancellationToken, Task> _copyCallback;
    private readonly Action _disposeCallback;
    private bool _disposed;

    internal AssetReference(string contentType, Func<Stream, CancellationToken, Task> copyCallback, Action disposeCallback)
    {
        ContentType = contentType;
        _copyCallback = copyCallback;
        _disposeCallback = disposeCallback;
    }

    /// <summary>
    /// Gets the MIME content type of the asset.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Copies the remaining asset content to the specified stream.
    /// </summary>
    /// <param name="destination">The destination stream to write content into.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        await _copyCallback(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the underlying gRPC call and associated resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCallback();
    }
}
