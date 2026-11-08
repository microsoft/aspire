// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a single backend registered with an Azure API Management service.
/// </summary>
[AspireExport]
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, BackendName = {BackendName}")]
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
}
