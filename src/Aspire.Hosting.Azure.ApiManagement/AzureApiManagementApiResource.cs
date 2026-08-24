// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an API hosted by an Azure API Management service.
/// </summary>
/// <param name="name">The name of the Aspire resource.</param>
/// <param name="apiName">The physical API name in API Management.</param>
/// <param name="path">The public gateway path for the API.</param>
/// <param name="displayName">The display name for the API.</param>
/// <param name="subscriptionRequired">Whether callers must provide an API Management subscription key.</param>
/// <param name="target">The backend compute resource.</param>
/// <param name="parent">The parent API Management service.</param>
[AspireExport]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementApiResource(
    string name,
    string apiName,
    string path,
    string displayName,
    bool subscriptionRequired,
    IComputeResource target,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    private readonly List<string> _inboundPolicyStatements = [];

    /// <summary>
    /// Gets the physical API name in API Management.
    /// </summary>
    public string ApiName { get; } = apiName;

    /// <summary>
    /// Gets the public gateway path for the API.
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// Gets the API display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets whether callers must provide an API Management subscription key.
    /// </summary>
    public bool SubscriptionRequired { get; } = subscriptionRequired;

    /// <summary>
    /// Gets the backend compute resource.
    /// </summary>
    internal IComputeResource Target { get; } = target;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    /// <summary>
    /// Gets the complete API-level policy document, when one has been configured.
    /// </summary>
    internal string? PolicyXml { get; set; }

    /// <summary>
    /// Gets the ordered API-level inbound policy statements.
    /// </summary>
    internal IReadOnlyList<string> InboundPolicyStatements => _inboundPolicyStatements;

    /// <summary>
    /// Gets the API operations.
    /// </summary>
    internal List<AzureApiManagementOperationResource> Operations { get; } = [];

    /// <summary>
    /// Adds an inbound policy statement.
    /// </summary>
    internal void AddInboundPolicyStatement(string policyXml) => _inboundPolicyStatements.Add(policyXml);
}
