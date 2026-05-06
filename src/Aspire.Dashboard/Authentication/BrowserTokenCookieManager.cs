// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Authentication.Cookies;

namespace Aspire.Dashboard.Authentication;

internal sealed class BrowserTokenCookieManager(string httpCookieName) : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public string? GetRequestCookie(HttpContext context, string key)
    {
        return _inner.GetRequestCookie(context, GetCookieName(context, key));
    }

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
    {
        _inner.AppendResponseCookie(context, GetCookieName(context, key), value, options);
    }

    public void DeleteCookie(HttpContext context, string key, CookieOptions options)
    {
        _inner.DeleteCookie(context, GetCookieName(context, key), options);

        if (context.Request.IsHttps && key != httpCookieName)
        {
            _inner.DeleteCookie(context, httpCookieName, options);
        }
    }

    private string GetCookieName(HttpContext context, string key)
    {
        // Keep HTTP browser-token cookies separate from HTTPS cookies so browser-specific localhost cookie behavior
        // can't cause a stale HTTPS cookie to shadow a fresh HTTP sign-in.
        return context.Request.IsHttps ? key : httpCookieName;
    }
}
