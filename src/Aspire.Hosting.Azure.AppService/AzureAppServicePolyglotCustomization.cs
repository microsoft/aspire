// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning.AppService;

namespace Aspire.Hosting;

internal static class AzureAppServicePolyglotCustomization
{
    [AspireExport("configureWebSiteSiteConfig", MethodName = "configureSiteConfig", Description = "Configures supported Azure App Service site settings.")]
    internal static void ConfigureSiteConfig(WebSite webSite, AzureAppServiceSiteConfig siteConfig)
    {
        ArgumentNullException.ThrowIfNull(webSite);
        ArgumentNullException.ThrowIfNull(siteConfig);

        webSite.SiteConfig ??= new SiteConfigProperties();
        ApplySiteConfig(webSite.SiteConfig, siteConfig);
    }

    [AspireExport("configureWebSiteSlotSiteConfig", MethodName = "configureSlotSiteConfig", Description = "Configures supported Azure App Service deployment slot site settings.")]
    internal static void ConfigureSiteConfig(WebSiteSlot webSiteSlot, AzureAppServiceSiteConfig siteConfig)
    {
        ArgumentNullException.ThrowIfNull(webSiteSlot);
        ArgumentNullException.ThrowIfNull(siteConfig);

        webSiteSlot.SiteConfig ??= new SiteConfigProperties();
        ApplySiteConfig(webSiteSlot.SiteConfig, siteConfig);
    }

    private static void ApplySiteConfig(SiteConfigProperties target, AzureAppServiceSiteConfig source)
    {
        if (source.IsAlwaysOn is { } isAlwaysOn)
        {
            target.IsAlwaysOn = isAlwaysOn;
        }
    }
}
