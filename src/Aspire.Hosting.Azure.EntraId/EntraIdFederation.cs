// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Cloud-specific values for trusting a managed identity from an Entra ID application registration.
/// </summary>
internal static class EntraIdFederation
{
    internal const string DefaultTokenExchangeAudience = "api://AzureADTokenExchange";

    /// <summary>
    /// Gets the audience that a managed identity's token must carry to be exchanged for the instance's cloud,
    /// or <see langword="null"/> when the cloud is not known.
    /// </summary>
    /// <remarks>
    /// The audiences are listed at
    /// <a href="https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity#important-considerations-and-restrictions">Configure an application to trust a managed identity</a>
    /// and in the Microsoft.Identity.Web documentation for <c>TokenExchangeUrl</c>.
    /// </remarks>
    internal static string? GetTokenExchangeAudience(string instance)
    {
        if (!Uri.TryCreate(instance, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Uri lowercases the host, so the comparisons can be ordinal.
        return uri.Host switch
        {
            "login.microsoftonline.com" => DefaultTokenExchangeAudience,
            "login.microsoftonline.us" => "api://AzureADTokenExchangeUSGov",
            "login.partner.microsoftonline.cn" or "login.chinacloudapi.cn" => "api://AzureADTokenExchangeChina",
            _ => null
        };
    }

    /// <summary>
    /// Gets the issuer of a managed identity's tokens for the instance's cloud and the given tenant.
    /// </summary>
    /// <remarks>
    /// The federated credential's issuer must equal the token's <c>iss</c> claim exactly, so equivalent forms of the
    /// instance URL must produce one issuer: the host is lowercased (<see cref="Uri.Host"/> does that) and a default
    /// port is dropped. For example <c>https://LOGIN.MICROSOFTONLINE.COM:443/</c> gives
    /// <c>https://login.microsoftonline.com/{tenant}/v2.0</c>.
    /// </remarks>
    internal static string GetIssuerPrefix(string instance)
    {
        var uri = new Uri(instance, UriKind.Absolute);
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"https://{authority}";
    }
}
