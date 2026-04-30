// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Documentation.Docs;

/// <summary>
/// Interface for caching aspire.dev documentation content with ETag support.
/// </summary>
internal interface IDocsCache : IDocumentContentCache
{
    /// <summary>
    /// Gets the cached parsed document index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached documents, or null if not found.</returns>
    Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the parsed document index in the cache.
    /// </summary>
    /// <param name="documents">The parsed documents to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the fingerprint for the source content used to build the cached index.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cached source fingerprint, or <c>null</c> if it is unavailable.</returns>
    Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the fingerprint for the source content used to build the cached index.
    /// </summary>
    /// <param name="fingerprint">The source fingerprint to cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default);
}
