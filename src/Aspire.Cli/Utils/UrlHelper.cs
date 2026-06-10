// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Cli.Utils;

internal static class UrlHelper
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="url"/> is an absolute HTTP or HTTPS URL.
    /// </summary>
    internal static bool IsHttpUrl([NotNullWhen(true)] string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }
}
