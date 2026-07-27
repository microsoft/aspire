// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Backend;

internal interface IDashboardLegacyApiProxy
{
    bool IsConfigured { get; }

    Task ProxyAsync(HttpContext context, string path);

    Task<bool> AuthorizeAsync(HttpContext context);
}

internal sealed class DashboardLegacyApiProxy(IConfiguration configuration) : IDashboardLegacyApiProxy
{
    private const string LegacyDashboardUrlKey = "DashboardBackend:LegacyDashboardUrl";
    private static readonly HttpClient s_client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

    public bool IsConfigured => TryGetLegacyDashboardUrl(out _);

    public async Task ProxyAsync(HttpContext context, string path)
    {
        using var request = CreateRequest(context, new HttpMethod(context.Request.Method), path);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
            if (context.Request.ContentLength is { } contentLength)
            {
                // Preserve a known length so the legacy import endpoint can reject an oversized
                // upload before either process reads or buffers its body. Chunked requests remain
                // chunked and are bounded by both Kestrel request limits.
                request.Content.Headers.ContentLength = contentLength;
            }
        }

        var credentials = DashboardRequestCredentials.From(context.Request);
        if (!string.IsNullOrEmpty(credentials.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", credentials.Cookie);
        }
        if (!string.IsNullOrEmpty(credentials.Authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", credentials.Authorization);
        }
        if (context.Request.Headers.Accept.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToArray());
        }
        if (context.Request.Headers.AcceptLanguage.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", context.Request.Headers.AcceptLanguage.ToArray());
        }
        if (context.Request.Headers.TryGetValue("X-Aspire-File-Name", out var fileName))
        {
            request.Headers.TryAddWithoutValidation("X-Aspire-File-Name", fileName.ToArray());
        }

        HttpResponseMessage response;
        try
        {
            response = await s_client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(
                $"The existing dashboard service is unavailable: {ex.Message}",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyHeader(response.Headers, context.Response.Headers);
            CopyHeader(response.Content.Headers, context.Response.Headers);
            context.Response.Headers.Remove("transfer-encoding");
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    public async Task<bool> AuthorizeAsync(HttpContext context)
    {
        var returnUrl = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        using var request = CreateRequest(
            context,
            HttpMethod.Get,
            $"api/dashboard/authenticate?returnUrl={Uri.EscapeDataString(returnUrl)}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var credentials = DashboardRequestCredentials.From(context.Request);
        if (!string.IsNullOrEmpty(credentials.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", credentials.Cookie);
        }
        if (!string.IsNullOrEmpty(credentials.Authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", credentials.Authorization);
        }

        try
        {
            using var response = await s_client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            CopyHeader(response.Headers, context.Response.Headers);
            CopyHeader(response.Content.Headers, context.Response.Headers);
            context.Response.Headers.Remove("transfer-encoding");
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            return false;
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(
                $"The existing dashboard authentication service is unavailable: {ex.Message}",
                context.RequestAborted).ConfigureAwait(false);
            return false;
        }
    }

    private HttpRequestMessage CreateRequest(HttpContext context, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(GetLegacyDashboardUrl(), path));
        if (!string.IsNullOrEmpty(context.Request.Host.Value))
        {
            // The legacy dashboard remains the authority for BrowserToken and OIDC while the
            // processes coexist. Preserve the browser-facing host so its relative login,
            // callback, logout, and cookie behavior remains on the AOT origin rather than
            // exposing or switching to the internal legacy port.
            request.Headers.Host = context.Request.Host.Value;
        }

        return request;
    }

    private Uri GetLegacyDashboardUrl()
    {
        if (!TryGetLegacyDashboardUrl(out var legacyDashboardUrl))
        {
            throw new InvalidOperationException(
                $"Configure {LegacyDashboardUrlKey} with the loopback URL of the existing dashboard.");
        }

        return legacyDashboardUrl;
    }

    private bool TryGetLegacyDashboardUrl(out Uri legacyDashboardUrl)
    {
        var configuredUrl = configuration[LegacyDashboardUrlKey];
        return Uri.TryCreate(configuredUrl, UriKind.Absolute, out legacyDashboardUrl!)
            && DashboardDevelopmentAccessPolicy.IsAllowedOrigin(legacyDashboardUrl.GetLeftPart(UriPartial.Authority));
    }

    private static void CopyHeader(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
        IHeaderDictionary destination)
    {
        foreach (var (name, values) in source)
        {
            destination[name] = values.ToArray();
        }
    }
}
