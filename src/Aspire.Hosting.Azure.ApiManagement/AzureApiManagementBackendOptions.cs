// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an Azure API Management backend.
/// </summary>
[AspireDto]
public sealed class AzureApiManagementBackendOptions
{
    /// <summary>
    /// Gets or sets the backend display title. The Aspire resource name is used when omitted.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or sets the protocol used to communicate with the backend.
    /// </summary>
    public AzureApiManagementBackendProtocol Protocol { get; init; } = AzureApiManagementBackendProtocol.Http;

    /// <summary>
    /// Gets or sets whether API Management validates the backend certificate chain.
    /// </summary>
    public bool ValidateCertificateChain { get; init; } = true;

    /// <summary>
    /// Gets or sets whether API Management validates the backend certificate hostname.
    /// </summary>
    public bool ValidateCertificateName { get; init; } = true;

    /// <summary>
    /// Gets or sets the managed-identity resource URI used to authenticate requests to the backend.
    /// </summary>
    /// <remarks>
    /// Set this to the Microsoft Entra resource URI expected by the backend, such as
    /// <c>https://storage.azure.com/</c>. Leave it unset for anonymous or policy-defined authentication.
    /// </remarks>
    public string? ManagedIdentityResource { get; init; }

    /// <summary>
    /// Gets or sets the backend circuit-breaker configuration.
    /// </summary>
    public AzureApiManagementCircuitBreakerOptions? CircuitBreaker { get; init; }
}
