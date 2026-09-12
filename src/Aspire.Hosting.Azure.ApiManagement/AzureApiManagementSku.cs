// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the pricing tier for an Azure API Management service.
/// </summary>
/// <remarks>
/// SKU capabilities differ substantially, especially for networking, scale, availability zones,
/// and multi-region deployment. See
/// <see href="https://learn.microsoft.com/azure/api-management/api-management-features">API Management tier features</see>.
/// </remarks>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public enum AzureApiManagementSku
{
    /// <summary>
    /// The serverless Consumption tier.
    /// </summary>
    Consumption,

    /// <summary>
    /// The non-production Developer tier.
    /// </summary>
    Developer,

    /// <summary>
    /// The classic Basic tier.
    /// </summary>
    Basic,

    /// <summary>
    /// The Basic v2 tier.
    /// </summary>
    BasicV2,

    /// <summary>
    /// The classic Standard tier.
    /// </summary>
    Standard,

    /// <summary>
    /// The Standard v2 tier.
    /// </summary>
    StandardV2,

    /// <summary>
    /// The classic Premium tier.
    /// </summary>
    Premium,

    /// <summary>
    /// The Premium v2 tier.
    /// </summary>
    PremiumV2,
}
