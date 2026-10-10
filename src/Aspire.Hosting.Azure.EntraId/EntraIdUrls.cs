// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Builds the links that the dashboard shows for an Entra ID application.
/// </summary>
internal static class EntraIdUrls
{
    /// <summary>
    /// Gets the URL of the OpenID Connect discovery document for the authority that Microsoft.Identity.Web uses.
    /// </summary>
    /// <param name="instance">The Entra ID instance, such as <c>https://login.microsoftonline.com/</c>.</param>
    /// <param name="tenant">
    /// The tenant segment of the authority: the home tenant for single-tenant apps, or the sign-in audience keyword,
    /// such as <c>organizations</c>, for the other audiences.
    /// </param>
    /// <remarks>
    /// Microsoft.Identity.Web builds the authority as <c>{Instance}/{TenantId}/v2.0</c> and discovers the endpoints
    /// and signing keys from this document, so opening it shows what the referencing apps will use.
    /// For more information, see
    /// <a href="https://learn.microsoft.com/entra/identity-platform/v2-protocols-oidc#find-your-apps-openid-configuration-document-uri">Find your app's OpenID configuration document URI</a>.
    /// </remarks>
    internal static string GetOpenIdConfigurationUrl(string instance, string tenant)
        => $"{instance.TrimEnd('/')}/{tenant}/v2.0/.well-known/openid-configuration";

    /// <summary>
    /// Gets a link to the app registration's Overview page in the portal for the instance's cloud,
    /// or <see langword="null"/> when that portal is not known.
    /// </summary>
    /// <param name="instance">The Entra ID instance, such as <c>https://login.microsoftonline.com/</c>.</param>
    /// <param name="homeTenantId">The ID or domain of the tenant where the app is registered.</param>
    /// <param name="clientId">The application (client) ID of the app registration.</param>
    internal static string? GetPortalUrl(string instance, string homeTenantId, string clientId)
    {
        if (!Uri.TryCreate(instance, UriKind.Absolute, out var uri) || GetPortalHost(uri.Host) is not { } portalHost)
        {
            return null;
        }

        // "#@{tenant}" makes the portal switch to the app's home tenant before opening the blade. Without it, the
        // portal looks for the registration in the signed-in user's default directory, which fails for developers
        // whose app is registered in another tenant.
        return $"https://{portalHost}/#@{homeTenantId}/view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{clientId}/isMSAApp~/false";
    }

    // Maps the instance's sign-in host to the portal of the same cloud. Uri lowercases the host, so the comparisons
    // can be ordinal. The national cloud hosts are listed at
    // https://learn.microsoft.com/entra/identity-platform/authentication-national-cloud#microsoft-entra-authentication-endpoints
    private static string? GetPortalHost(string loginHost) => loginHost switch
    {
        "login.microsoftonline.com" or "login.microsoft.com" or "login.windows.net" => "portal.azure.com",
        "login.microsoftonline.us" => "portal.azure.us",
        "login.chinacloudapi.cn" or "login.partner.microsoftonline.cn" => "portal.azure.cn",

        // External tenants sign users in at "{tenant}.ciamlogin.com" and are managed in the Microsoft Entra admin center.
        _ when loginHost.EndsWith(".ciamlogin.com", StringComparison.Ordinal) => "entra.microsoft.com",

        // Private clouds, emulators and proxies have no known portal, and a link to another cloud's portal would mislead.
        _ => null
    };
}
