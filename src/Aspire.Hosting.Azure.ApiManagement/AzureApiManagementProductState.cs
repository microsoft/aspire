// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the publication state of an API Management product.
/// </summary>
public enum AzureApiManagementProductState
{
    /// <summary>
    /// The product is not visible to subscribers.
    /// </summary>
    NotPublished,

    /// <summary>
    /// The product is visible to subscribers.
    /// </summary>
    Published,
}
