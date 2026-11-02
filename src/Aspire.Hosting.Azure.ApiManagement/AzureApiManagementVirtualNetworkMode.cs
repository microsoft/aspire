// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies how a classic Azure API Management service is injected into a virtual network.
/// </summary>
public enum AzureApiManagementVirtualNetworkMode
{
    /// <summary>
    /// Exposes API Management through a public load balancer while allowing access to private backends.
    /// </summary>
    External,

    /// <summary>
    /// Exposes API Management only through an internal load balancer.
    /// </summary>
    Internal,
}
