// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Bundles;

/// <summary>
/// Provides access to the bundle payload stream for extraction.
/// </summary>
internal interface IBundlePayloadProvider
{
    /// <summary>
    /// Gets whether a bundle payload is available.
    /// </summary>
    bool HasPayload { get; }

    /// <summary>
    /// Opens a read-only stream over the bundle payload.
    /// Returns <see langword="null"/> if no payload is available.
    /// Each call must return a fresh, independently consumable stream.
    /// </summary>
    Stream? OpenPayload();
}
