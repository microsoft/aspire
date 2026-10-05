// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Web;

namespace Aspire.Hosting.Utils;

internal static class StringUtils
{
    public static bool TryGetUriFromDelimitedString([NotNullWhen(true)] string? input, string delimiter, [NotNullWhen(true)] out Uri? uri)
    {
        if (!string.IsNullOrEmpty(input)
            && input.Split(delimiter) is { Length: > 0 } splitInput
            && Uri.TryCreate(splitInput[0], UriKind.Absolute, out uri))
        {
            return true;
        }
        else
        {
            uri = null;
            return false;
        }
    }

    public static string Escape(string value)
    {
        return HttpUtility.UrlEncode(value);
    }

    public static string Unescape(string value)
    {
        return HttpUtility.UrlDecode(value);
    }

    public static string RemoveSuffix(this string value, string suffix)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(suffix);

        return value.EndsWith(suffix, StringComparison.Ordinal)
            ? value[..^suffix.Length]
            : value;
    }

    public static string? FirstNonEmpty(string? first, string? second)
    {
        return !string.IsNullOrEmpty(first)
            ? first
            : !string.IsNullOrEmpty(second)
                ? second
                : null;
    }
}
