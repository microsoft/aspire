// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an API hosted by an Azure API Management service.
/// </summary>
[AspireDto]
public sealed class AzureApiManagementApiOptions
{
    /// <summary>
    /// Gets the API display name. The Aspire resource name is used when null.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets whether callers must provide an API Management subscription key. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SubscriptionRequired { get; init; } = true;
}
