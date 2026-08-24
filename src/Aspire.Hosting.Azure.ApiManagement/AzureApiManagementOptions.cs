// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an Azure API Management service.
/// </summary>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class AzureApiManagementOptions
{
    /// <summary>
    /// Gets the publisher email address shown by API Management.
    /// </summary>
    public required string PublisherEmail { get; init; }

    /// <summary>
    /// Gets the publisher name shown by API Management.
    /// </summary>
    public string PublisherName { get; init; } = "Aspire";

    /// <summary>
    /// Gets the API Management pricing tier.
    /// </summary>
    public AzureApiManagementSku Sku { get; init; } = AzureApiManagementSku.Developer;

    /// <summary>
    /// Gets the number of capacity units.
    /// </summary>
    /// <remarks>
    /// Consumption requires zero capacity units. Developer requires one. Other tiers have SKU-specific limits.
    /// </remarks>
    public int Capacity { get; init; } = 1;
}
