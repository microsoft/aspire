// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;

namespace Aspire.Cli.Documentation;

/// <summary>
/// Computes stable fingerprints for cached documentation source content.
/// </summary>
internal static class SourceContentFingerprint
{
    /// <summary>
    /// Computes a stable lowercase hex fingerprint for the specified content.
    /// </summary>
    /// <param name="content">The source content to fingerprint.</param>
    /// <returns>A stable lowercase hex fingerprint.</returns>
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var xxHash = new XxHash3();
        xxHash.Append(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(xxHash.GetCurrentHash()).ToLowerInvariant();
    }
}
