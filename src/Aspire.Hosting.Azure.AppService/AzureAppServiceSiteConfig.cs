// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Polyglot-friendly subset of Azure App Service site configuration.
/// </summary>
[AspireDto]
internal sealed class AzureAppServiceSiteConfig
{
    /// <summary>
    /// Gets or sets whether the App Service app is always loaded.
    /// </summary>
    public bool? IsAlwaysOn { get; set; }
}
