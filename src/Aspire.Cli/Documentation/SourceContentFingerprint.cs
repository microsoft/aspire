// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Aspire.Cli.Documentation;

/// <summary>
/// Computes stable fingerprints for cached documentation source content.
/// </summary>
internal static class SourceContentFingerprint
{
    /// <summary>
    /// Computes a stable lowercase hex fingerprint for the specified content
    /// and the caller's index schema version.
    /// </summary>
    /// <remarks>
    /// The schema version is hashed into the result so that bumping the caller's
    /// constant invalidates every previously-cached fingerprint, even when the
    /// source content has not changed. This is how callers force a re-parse on
    /// next launch when the parser or index output shape changes.
    /// </remarks>
    /// <param name="content">The source content to fingerprint.</param>
    /// <param name="schemaVersion">
    /// The caller-owned index schema version. Bump this constant whenever the
    /// downstream parsed/indexed representation changes in a way that would
    /// produce different cache contents for the same input.
    /// </param>
    /// <returns>A stable lowercase hex fingerprint.</returns>
    public static string Compute(string content, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(content);

        var xxHash = new XxHash3();

        // Mix the schema version into the hash before the content so callers can
        // invalidate prior caches by bumping their constant.
        Span<byte> versionBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(versionBytes, schemaVersion);
        xxHash.Append(versionBytes);

        xxHash.Append(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(xxHash.GetCurrentHash()).ToLowerInvariant();
    }
}
