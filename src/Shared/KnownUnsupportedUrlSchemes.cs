// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// URL schemes that terminals and browsers don't support opening as links.
/// </summary>
/// <remarks>
/// This is a deny list because custom schemes could hand off the link to an app registered with the OS.
/// For example, vscode://.
/// </remarks>
internal static class KnownUnsupportedUrlSchemes
{
    public static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gopher",
        "ws",
        "wss",
        "news",
        "nntp",
        "telnet",
        "tcp",
        "redis",
        "rediss"
    };

    /// <summary>
    /// Returns <c>true</c> when the URL scheme is known to be linkable (i.e. not in the unsupported set).
    /// </summary>
    public static bool IsLinkableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !Schemes.Contains(uri.Scheme);
}
