// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the protocol used to communicate with an API Management backend.
/// </summary>
public enum AzureApiManagementBackendProtocol
{
    /// <summary>
    /// The backend uses HTTP or HTTPS.
    /// </summary>
    Http,

    /// <summary>
    /// The backend uses SOAP.
    /// </summary>
    Soap,
}
