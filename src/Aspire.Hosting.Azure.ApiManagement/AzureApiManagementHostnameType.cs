// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies an Azure API Management custom-hostname endpoint.
/// </summary>
public enum AzureApiManagementHostnameType
{
    /// <summary>
    /// The configuration API endpoint.
    /// </summary>
    ConfigurationApi,

    /// <summary>
    /// The API gateway endpoint.
    /// </summary>
    Proxy,

    /// <summary>
    /// The developer portal endpoint.
    /// </summary>
    DeveloperPortal,

    /// <summary>
    /// The legacy developer portal endpoint.
    /// </summary>
    Portal,

    /// <summary>
    /// The management endpoint.
    /// </summary>
    Management,

    /// <summary>
    /// The source-control endpoint.
    /// </summary>
    Scm,
}
