// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an Azure API Management subscription.
/// </summary>
[AspireDto]
public sealed class AzureApiManagementSubscriptionOptions
{
    /// <summary>
    /// Gets or sets whether tracing is allowed for the subscription.
    /// </summary>
    public bool AllowTracing { get; set; }

    /// <summary>
    /// Gets or sets the subscription state.
    /// </summary>
    public AzureApiManagementSubscriptionState State { get; set; } = AzureApiManagementSubscriptionState.Active;
}
