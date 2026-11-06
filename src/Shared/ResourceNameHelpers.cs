// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Creates bounded resource names while retaining a readable prefix and stable identity.
/// </summary>
internal static class ResourceNameHelpers
{
    public static string GetEndpointPropertyInputName(string resourceName, string endpointName, string property, int maxLength)
    {
        var name = $"{resourceName}-{endpointName}-{property}";
        if (endpointName.Contains('-'))
        {
            // "a-b"/"c" and "a"/"b-c" have the same readable prefix. Encode the
            // boundary so independently published environments and request order agree.
            var identity = $"{resourceName.ToUpperInvariant()}\0{endpointName.ToUpperInvariant()}\0{property}";
            name = $"{name}-{GetHash(identity)}";
        }

        return TruncateWithHash(name, maxLength);
    }

    /// <summary>
    /// Preserves names that fit, or shortens them using a prefix and a hash of the full name.
    /// </summary>
    public static string TruncateWithHash(string name, int maxLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        // Keep at least one readable character, a separator, and the full 64-bit hash.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 18);

        if (name.Length <= maxLength)
        {
            return name;
        }

        var hash = GetHash(name);

        return $"{name[..(maxLength - hash.Length - 1)]}-{hash}";
    }

    private static string GetHash(string name) =>
        XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(name)).ToString("x16", CultureInfo.InvariantCulture);
}
