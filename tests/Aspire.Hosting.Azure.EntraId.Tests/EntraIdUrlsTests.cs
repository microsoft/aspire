// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdUrlsTests
{
    // Placeholder IDs from the Microsoft Learn documentation. They are well-formed but identify nothing.
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    [Theory]
    [InlineData("https://login.microsoftonline.com/", TenantId, $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration")]
    [InlineData("https://login.microsoftonline.com", TenantId, $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration")]
    [InlineData("https://login.microsoftonline.com/", "organizations", "https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration")]
    [InlineData("https://contoso.ciamlogin.com/", "contoso.onmicrosoft.com", "https://contoso.ciamlogin.com/contoso.onmicrosoft.com/v2.0/.well-known/openid-configuration")]
    public void GetOpenIdConfigurationUrl_AppendsTenantAndDiscoveryPath(string instance, string tenant, string expectedUrl)
    {
        Assert.Equal(expectedUrl, EntraIdUrls.GetOpenIdConfigurationUrl(instance, tenant));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/", "portal.azure.com")]
    [InlineData("https://login.microsoft.com/", "portal.azure.com")]
    [InlineData("https://login.windows.net/", "portal.azure.com")]
    // Host names are case-insensitive.
    [InlineData("https://LOGIN.MicrosoftOnline.com/", "portal.azure.com")]
    [InlineData("https://login.microsoftonline.us/", "portal.azure.us")]
    [InlineData("https://login.chinacloudapi.cn/", "portal.azure.cn")]
    [InlineData("https://login.partner.microsoftonline.cn/", "portal.azure.cn")]
    [InlineData("https://contoso.ciamlogin.com/", "entra.microsoft.com")]
    public void GetPortalUrl_OpensAppRegistrationInPortalOfInstanceCloud(string instance, string expectedPortalHost)
    {
        Assert.Equal(
            $"https://{expectedPortalHost}/#@{TenantId}/view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{ClientId}/isMSAApp~/false",
            EntraIdUrls.GetPortalUrl(instance, TenantId, ClientId));
    }

    [Theory]
    [InlineData("https://login.contoso.example/")]
    [InlineData("https://localhost:8443/")]
    // Only subdomains of ciamlogin.com are sign-in hosts of external tenants.
    [InlineData("https://ciamlogin.com/")]
    [InlineData("https://contosociamlogin.com/")]
    [InlineData("not a url")]
    public void GetPortalUrl_ReturnsNullWhenPortalIsUnknown(string instance)
    {
        Assert.Null(EntraIdUrls.GetPortalUrl(instance, TenantId, ClientId));
    }
}
