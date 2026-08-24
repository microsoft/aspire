// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an operation in an Azure API Management API.
/// </summary>
/// <param name="name">The name of the Aspire resource.</param>
/// <param name="operationName">The physical operation name in API Management.</param>
/// <param name="method">The HTTP method, or <c>*</c> to match every method.</param>
/// <param name="urlTemplate">The URL template relative to the API path.</param>
/// <param name="displayName">The operation display name.</param>
/// <param name="parent">The parent API.</param>
[AspireExport]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementOperationResource(
    string name,
    string operationName,
    string method,
    string urlTemplate,
    string displayName,
    AzureApiManagementApiResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementApiResource>
{
    private readonly List<string> _inboundPolicyStatements = [];

    /// <summary>
    /// Gets the physical operation name in API Management.
    /// </summary>
    public string OperationName { get; } = operationName;

    /// <summary>
    /// Gets the HTTP method.
    /// </summary>
    public string Method { get; } = method;

    /// <summary>
    /// Gets the URL template relative to the API path.
    /// </summary>
    public string UrlTemplate { get; } = urlTemplate;

    /// <summary>
    /// Gets the operation display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the parent API.
    /// </summary>
    public AzureApiManagementApiResource Parent { get; } = parent;

    /// <summary>
    /// Gets the complete operation-level policy document, when one has been configured.
    /// </summary>
    internal string? PolicyXml { get; set; }

    /// <summary>
    /// Gets the ordered operation-level inbound policy statements.
    /// </summary>
    internal IReadOnlyList<string> InboundPolicyStatements => _inboundPolicyStatements;

    /// <summary>
    /// Adds an inbound policy statement.
    /// </summary>
    internal void AddInboundPolicyStatement(string policyXml) => _inboundPolicyStatements.Add(policyXml);
}
