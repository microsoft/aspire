// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Interface for caching API documentation content and the parsed API index.
/// </summary>
internal interface IApiDocsCache : IDocumentContentCache
{
    /// <summary>
    /// Gets the cached parsed API index.
    /// </summary>
    Task<ApiReferenceItem[]?> GetIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the parsed API index in the cache.
    /// </summary>
    Task SetIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the fingerprint for the sitemap content used to build the cached index.
    /// </summary>
    Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the fingerprint for the sitemap content used to build the cached index.
    /// </summary>
    Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the cached parsed C# member index.
    /// </summary>
    Task<ApiReferenceItem[]?> GetMemberIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the parsed C# member index in the cache.
    /// </summary>
    Task SetMemberIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the fingerprint for the sitemap content used to build the cached member index.
    /// </summary>
    Task<string?> GetMemberIndexSourceFingerprintAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the fingerprint for the sitemap content used to build the cached member index.
    /// </summary>
    Task SetMemberIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default);
}
