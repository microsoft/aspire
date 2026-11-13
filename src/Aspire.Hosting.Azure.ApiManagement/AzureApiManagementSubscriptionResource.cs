// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure API Management subscription scoped to a product.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, SubscriptionName = {SubscriptionName}")]
public class AzureApiManagementSubscriptionResource(
    string name,
    string subscriptionName,
    string displayName,
    AzureApiManagementSubscriptionOptions options,
    AzureApiManagementProductResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementProductResource>
{
    /// <summary>
    /// Gets the physical subscription identifier in API Management.
    /// </summary>
    public string SubscriptionName { get; } = subscriptionName;

    /// <summary>
    /// Gets the subscription display name.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the subscription options.
    /// </summary>
    public AzureApiManagementSubscriptionOptions Options { get; } = options;

    /// <summary>
    /// Gets the parent API Management product.
    /// </summary>
    public AzureApiManagementProductResource Parent { get; } = parent;
}
