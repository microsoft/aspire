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

        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(name))
            .ToString("x16", CultureInfo.InvariantCulture);

        return $"{name[..(maxLength - hash.Length - 1)]}-{hash}";
    }
}
