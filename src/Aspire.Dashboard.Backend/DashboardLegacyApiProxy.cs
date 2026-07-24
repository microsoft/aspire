// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Backend;

internal interface IDashboardLegacyApiProxy
{
    Task ProxyAsync(HttpContext context, string path);
}

internal sealed class DashboardLegacyApiProxy(IConfiguration configuration) : IDashboardLegacyApiProxy
{
    private const string LegacyDashboardUrlKey = "DashboardBackend:LegacyDashboardUrl";
    private static readonly HttpClient s_client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

    public async Task ProxyAsync(HttpContext context, string path)
    {
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            new Uri(GetLegacyDashboardUrl(), path));
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
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

        using var response = await s_client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyHeader(response.Headers, context.Response.Headers);
        CopyHeader(response.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private Uri GetLegacyDashboardUrl()
    {
        var configuredUrl = configuration[LegacyDashboardUrlKey];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var legacyDashboardUrl)
            || !DashboardDevelopmentAccessPolicy.IsAllowedOrigin(legacyDashboardUrl.GetLeftPart(UriPartial.Authority)))
        {
            throw new InvalidOperationException(
                $"Configure {LegacyDashboardUrlKey} with the loopback URL of the existing dashboard.");
        }

        return legacyDashboardUrl;
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
