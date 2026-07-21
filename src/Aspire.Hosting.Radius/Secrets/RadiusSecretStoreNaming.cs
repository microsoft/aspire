// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Naming helpers for Radius secret stores: validating a store name.
/// </summary>
/// <remarks>
/// A secret-store name is used verbatim as a Bicep symbol/resource name, a UCP-ID segment,
/// a Radius-created <c>Secret</c> name, and — in publish mode — as an Aspire resource name
/// (the store builder calls <c>AddResource</c>, which enforces <see cref="ApplicationModel.ModelName"/>).
/// To avoid a mode-dependent contract (run mode uses an unregistered builder and skips that check),
/// the grammar is a strict subset of Aspire's resource-name grammar: 1-64 characters of
/// <em>lowercase</em> ASCII letters, digits, and <c>-</c>, starting with a letter, with no
/// consecutive hyphens and no trailing hyphen. Lowercase is required (rather than the mixed case
/// Aspire otherwise permits) because an inline store has no <c>properties.resource</c>, so Radius
/// uses the store name directly as the backing Kubernetes <c>Secret</c> name and Kubernetes rejects
/// non-DNS-1123 (uppercase) Secret names at deploy time. Keeping the grammar lowercase-only fails
/// such names fast at the API/publish boundary instead. It additionally rejects Windows reserved
/// device names, because the name can also become a filesystem path segment for a copied manifest
/// and must be materializable on every platform.
/// </remarks>
internal static class RadiusSecretStoreNaming
{
    /// <summary>
    /// The maximum length of a Radius secret-store name, matching Aspire's resource-name limit
    /// (<c>ModelName.DefaultMaxLength</c>).
    /// </summary>
    internal const int MaxNameLength = 64;

    // Windows reserved device names. A store name can be used as a `<name>` artifact path
    // segment, so a name like `CON` or `NUL` cannot be materialized on Windows even though
    // Radius/UCP itself would accept it. Consecutive-hyphen/dot forms cannot occur under the
    // resource-name grammar, so a plain case-insensitive comparison of the whole name suffices.
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
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        // Mirror ModelName.TryValidateName's default rules, but restrict letters to lowercase so a
        // name accepted here is also accepted by AddResource in publish mode AND is a valid DNS-1123
        // label — an inline store's name becomes the backing Kubernetes Secret name verbatim, and
        // Kubernetes rejects uppercase Secret names at deploy time.
        if (!char.IsAsciiLetterLower(name[0]) || name[^1] == '-')
        {
            return false;
        }

        var previousHyphen = false;
        foreach (var c in name)
        {
            if (c == '-')
            {
                if (previousHyphen)
                {
                    return false;
                }
                previousHyphen = true;
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                previousHyphen = false;
            }
            else
            {
                return false;
            }
        }

        return !s_reservedDeviceNames.Contains(name);
    }
}
