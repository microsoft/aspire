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
    private int? _capacity;
    private AzureApiManagementSku? _sku;

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
    /// <remarks>
    /// Defaults to Developer for a new service. When adopting an existing service, explicitly set this
    /// property to its actual SKU; Aspire uses this metadata to validate supported child resources.
    /// </remarks>
    public AzureApiManagementSku Sku
    {
        get => _sku ?? AzureApiManagementSku.Developer;
        init => _sku = value;
    }

    internal bool HasExplicitSku => _sku.HasValue;

    /// <summary>
    /// Gets the number of capacity units.
    /// </summary>
    /// <remarks>
    /// Consumption requires zero capacity units. Developer requires one. Other tiers have SKU-specific limits.
    /// </remarks>
    public int Capacity
    {
        get => _capacity ?? (Sku is AzureApiManagementSku.Consumption ? 0 : 1);
        init => _capacity = value;
    }
}
