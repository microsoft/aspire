// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Utils;

/// <summary>
/// Helpers for validating redirect URLs.
/// </summary>
internal static class UrlValidationHelper
{
    /// <summary>
    /// Checks whether a URL is safe to use as a redirect target.
    /// The URL must be a valid URI and a local path (not absolute or protocol-relative).
    /// </summary>
    internal static bool IsSafeRedirectUrl([NotNullWhen(true)] string? url)
    {
        return Uri.TryCreate(url, UriKind.Relative, out _) && IsLocalUrl(url);
    }

    // Copied from ASP.NET Core's IsLocalUrl implementation:
    // https://github.com/dotnet/aspnetcore/blob/7cbda0e023075490b4365a0754ca410ce6eff59a/src/Shared/ResultsHelpers/SharedUrlHelper.cs#L33
    internal static bool IsLocalUrl([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        // Allows "/" or "/foo" but not "//" or "/\".
        if (url[0] == '/')
        {
            // url is exactly "/"
            if (url.Length == 1)
            {
                return true;
            }

            // url doesn't start with "//" or "/\"
            if (url[1] != '/' && url[1] != '\\')
            {
                return !HasControlCharacter(url.AsSpan(1));
            }

            return false;
        }

        // Allows "~/" or "~/foo" but not "~//" or "~/\".
        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            // url is exactly "~/"
            if (url.Length == 2)
            {
                return true;
            }

            // url doesn't start with "~//" or "~/\"
            if (url[2] != '/' && url[2] != '\\')
            {
                return !HasControlCharacter(url.AsSpan(2));
            }

            return false;
        }

        return false;

        static bool HasControlCharacter(ReadOnlySpan<char> readOnlySpan)
        {
            // URLs may not contain ASCII control characters.
            for (var i = 0; i < readOnlySpan.Length; i++)
            {
                if (char.IsControl(readOnlySpan[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
