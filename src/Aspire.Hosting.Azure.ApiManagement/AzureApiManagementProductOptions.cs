// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an Azure API Management product.
/// </summary>
[AspireDto]
public sealed class AzureApiManagementProductOptions
{
    /// <summary>
    /// Gets the product description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the terms of use presented to subscribers.
    /// </summary>
    public string? Terms { get; init; }

    /// <summary>
    /// Gets whether a subscription is required to access APIs in the product.
    /// </summary>
    public bool SubscriptionRequired { get; init; } = true;

    /// <summary>
    /// Gets whether subscription requests require administrator approval.
    /// </summary>
    public bool ApprovalRequired { get; init; }

    /// <summary>
    /// Gets the maximum number of subscriptions a user may create for the product. A null value allows unlimited subscriptions.
    /// </summary>
    public int? SubscriptionsLimit { get; init; }

    /// <summary>
    /// Gets the product publication state.
    /// </summary>
    public AzureApiManagementProductState State { get; init; } = AzureApiManagementProductState.Published;
}
