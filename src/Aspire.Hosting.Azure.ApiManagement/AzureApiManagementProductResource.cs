// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure API Management product.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ProductName = {ProductName}")]
public class AzureApiManagementProductResource(
    string name,
    string productName,
    string displayName,
    AzureApiManagementProductOptions options,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical product identifier in API Management.
    /// </summary>
    public string ProductName { get; } = productName;

    /// <summary>
    /// Gets the product display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the product options.
    /// </summary>
    public AzureApiManagementProductOptions Options { get; } = options;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    internal List<AzureApiManagementApiResource> Apis { get; } = [];

    internal List<AzureApiManagementSubscriptionResource> Subscriptions { get; } = [];
}
