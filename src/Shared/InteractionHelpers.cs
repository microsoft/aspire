// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire;

internal static partial class InteractionHelpers
{
    // Chosen to balance between being long enough for most normal use but provides a default limit
    // to prevent possible abuse of interactions API.
    public const int DefaultMaxLength = 8000;

    // Limits multi-file inputs to a practical number of files while preventing unbounded submissions.
    public const int MaxFileCount = 100;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    public static int GetMaxLength(int? configuredInputLength)
    {
        // An unconfigured max length uses the default.
        if (configuredInputLength is null || configuredInputLength == 0)
        {
            return DefaultMaxLength;
        }

        return configuredInputLength.Value;
    }

    public static int GetMaxFileCount(bool allowMultipleFiles) => allowMultipleFiles ? MaxFileCount : 1;

    public static string LabelToName(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        var name = label.ToLowerInvariant();

        name = NonAlphanumericRegex().Replace(name, "_");
        name = name.Trim('_');

        return name;
    }
}
