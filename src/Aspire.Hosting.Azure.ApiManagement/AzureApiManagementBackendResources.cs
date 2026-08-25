// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a single backend registered with an Azure API Management service.
/// </summary>
[AspireExport]
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, BackendName = {BackendName}")]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementBackendResource(
    string name,
    string backendName,
    ReferenceExpression uriExpression,
    AzureApiManagementBackendOptions options,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical backend identifier in API Management.
    /// </summary>
    public string BackendName { get; } = backendName;

    /// <summary>
    /// Gets the deferred backend URI.
    /// </summary>
    public ReferenceExpression UriExpression { get; } = uriExpression;

    /// <summary>
    /// Gets the backend configuration.
    /// </summary>
    public AzureApiManagementBackendOptions Options { get; } = options;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    internal List<AzureApiManagementBackendRoleAssignment> RoleAssignments { get; } = [];
}

/// <summary>
/// Represents a load-balancing pool of Azure API Management backends.
/// </summary>
[AspireExport]
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, BackendPoolName = {BackendPoolName}")]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementBackendPoolResource(
    string name,
    string backendPoolName,
    string displayName,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical backend-pool identifier in API Management.
    /// </summary>
    public string BackendPoolName { get; } = backendPoolName;

    /// <summary>
    /// Gets the backend-pool display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    internal List<AzureApiManagementBackendPoolMember> Backends { get; } = [];
}

/// <summary>
/// Configures an Azure API Management backend.
/// </summary>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
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

/// <summary>
/// Configures a circuit breaker for an Azure API Management backend.
/// </summary>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class AzureApiManagementCircuitBreakerOptions
{
    /// <summary>
    /// Gets or sets the circuit-breaker rule name.
    /// </summary>
    public string Name { get; init; } = "default";

    /// <summary>
    /// Gets or sets the number of failures that opens the circuit.
    /// </summary>
    public int FailureCount { get; init; } = 1;

    /// <summary>
    /// Gets or sets the interval, in seconds, during which failures are counted.
    /// </summary>
    public int FailureIntervalSeconds { get; init; } = 10;

    /// <summary>
    /// Gets or sets the HTTP status-code ranges treated as failures.
    /// </summary>
    public AzureApiManagementStatusCodeRange[] StatusCodeRanges { get; init; } = [];

    /// <summary>
    /// Gets or sets how many seconds the circuit remains open.
    /// </summary>
    public int TripDurationSeconds { get; init; } = 10;

    /// <summary>
    /// Gets or sets whether API Management honors the backend's <c>Retry-After</c> header.
    /// </summary>
    public bool AcceptRetryAfter { get; init; }
}

/// <summary>
/// Represents an inclusive HTTP status-code range used by an API Management circuit breaker.
/// </summary>
/// <param name="Minimum">The lowest status code in the range.</param>
/// <param name="Maximum">The highest status code in the range.</param>
[AspireDto]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed record AzureApiManagementStatusCodeRange(int Minimum, int Maximum);

/// <summary>
/// Specifies the protocol used to communicate with an API Management backend.
/// </summary>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
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

internal sealed record AzureApiManagementBackendPoolMember(
    AzureApiManagementBackendResource Backend,
    int Priority,
    int Weight);

internal sealed record AzureApiManagementBackendRoleAssignment(
    AzureProvisioningResource Target,
    object Role);
