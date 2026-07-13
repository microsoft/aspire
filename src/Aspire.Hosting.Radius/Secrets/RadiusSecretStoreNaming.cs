// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Naming helpers for Radius secret stores: validating a store name.
/// </summary>
/// <remarks>
/// A secret-store name is used verbatim as a Bicep symbol/resource name, a UCP-ID segment,
/// and a Radius-created <c>Secret</c> name, so it must be a single valid resource-name
/// segment. The grammar mirrors UCP/Azure Resource Manager resource names: 1-90 characters
/// of ASCII letters, digits, <c>-</c>, <c>_</c>, and <c>.</c>, not starting or ending with
/// <c>.</c>, with no consecutive <c>.</c>, and not a Windows reserved device name (the name
/// can also become a filesystem path segment for a copied manifest, so it must be materializable
/// on every platform).
/// </remarks>
internal static class RadiusSecretStoreNaming
{
    /// <summary>
    /// The maximum length of a Radius secret-store name, matching the UCP/Azure Resource
    /// Manager resource-name limit.
    /// </summary>
    internal const int MaxNameLength = 90;

    // Windows reserved device names. A store name can be used as a `<name>` artifact path
    // segment, so a name like `CON` or `NUL` (with or without an extension, e.g. `CON.bicep`)
    // cannot be materialized on Windows even though Radius/UCP itself would accept it. Matching
    // is case-insensitive.
    private static readonly HashSet<string> s_reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Validates that <paramref name="name"/> is a safe Radius secret-store name.
    /// </summary>
    internal static bool IsValidName([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        // "." and ".." are relative-path tokens; a leading/trailing '.' is also disallowed
        // by ARM/UCP and would produce a surprising directory segment.
        if (name is "." or ".." || name[0] == '.' || name[^1] == '.')
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return false;
            }
        }

        // A reserved device name is reserved regardless of any extension, so compare the
        // base name before the first '.' (e.g. `CON.bicep` -> `CON`).
        var dotIndex = name.IndexOf('.', StringComparison.Ordinal);
        var baseName = dotIndex < 0 ? name : name[..dotIndex];
        return !s_reservedDeviceNames.Contains(baseName);
    }
}
