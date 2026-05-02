// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal enum BrowserLogsBrowserFamily
{
    Chromium,
    Safari
}

internal static class BrowserLogsBrowserResolver
{
    public static BrowserLogsBrowserFamily ResolveFamily(string browser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browser);
        return SafariBrowserResolver.IsSafariBrowser(browser) ? BrowserLogsBrowserFamily.Safari : BrowserLogsBrowserFamily.Chromium;
    }

    public static string? TryResolveExecutable(string browser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browser);
        return ResolveFamily(browser) switch
        {
            BrowserLogsBrowserFamily.Safari => SafariBrowserResolver.TryResolveDriver(browser)?.DriverPath,
            _ => ChromiumBrowserResolver.TryResolveExecutable(browser)
        };
    }
}
