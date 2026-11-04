// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a load-balancing pool of Azure API Management backends.
/// </summary>
[AspireExport]
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, BackendPoolName = {BackendPoolName}")]
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

internal sealed record AzureApiManagementBackendPoolMember(
    AzureApiManagementBackendResource Backend,
    int Priority,
    int Weight);
