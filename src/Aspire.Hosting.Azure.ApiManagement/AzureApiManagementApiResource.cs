// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an API hosted by an Azure API Management service.
/// </summary>
[AspireExport]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementApiResource : Resource, IResourceWithParent<AzureApiManagementResource>
{
    private readonly List<string> _inboundPolicyStatements = [];

    /// <summary>
    /// Initializes an API that routes to a compute resource.
    /// </summary>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="apiName">The physical API name in API Management.</param>
    /// <param name="path">The public gateway path for the API.</param>
    /// <param name="displayName">The display name for the API.</param>
    /// <param name="subscriptionRequired">Whether callers must provide an API Management subscription key.</param>
    /// <param name="target">The backend compute resource.</param>
    /// <param name="parent">The parent API Management service.</param>
    public AzureApiManagementApiResource(
        string name,
        string apiName,
        string path,
        string displayName,
        bool subscriptionRequired,
        IComputeResource target,
        AzureApiManagementResource parent)
        : this(name, apiName, path, displayName, subscriptionRequired, parent)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    internal AzureApiManagementApiResource(
        string name,
        string apiName,
        string path,
        string displayName,
        bool subscriptionRequired,
        AzureApiManagementResource parent)
        : base(name)
    {
        ApiName = apiName;
        Path = path;
        DisplayName = displayName;
        SubscriptionRequired = subscriptionRequired;
        Parent = parent;
    }

    /// <summary>
    /// Gets the physical API name in API Management.
    /// </summary>
    public string ApiName { get; }

    /// <summary>
    /// Gets the public gateway path for the API.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the API display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets whether callers must provide an API Management subscription key.
    /// </summary>
    public bool SubscriptionRequired { get; }

    /// <summary>
    /// Gets the backend compute resource.
    /// </summary>
    internal IComputeResource? Target { get; }

    /// <summary>
    /// Gets or sets the backend or backend pool used by this API.
    /// </summary>
    internal IResource? Backend { get; set; }

    /// <summary>
    /// Gets or sets the API-level Application Insights diagnostic.
    /// </summary>
    internal AzureApiManagementDiagnostic? Diagnostic { get; set; }

    /// <summary>
    /// Gets or sets the OpenAPI document imported for this API.
    /// </summary>
    internal AzureApiManagementOpenApiSource? OpenApiSource { get; set; }

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; }

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

internal abstract record AzureApiManagementOpenApiSource(AzureApiManagementOpenApiFormat Format);

internal sealed record AzureApiManagementOpenApiContent(
    string Content,
    AzureApiManagementOpenApiFormat Format)
    : AzureApiManagementOpenApiSource(Format);

internal sealed record AzureApiManagementOpenApiEndpoint(
    string Path,
    string? EndpointName,
    AzureApiManagementOpenApiFormat Format)
    : AzureApiManagementOpenApiSource(Format);
