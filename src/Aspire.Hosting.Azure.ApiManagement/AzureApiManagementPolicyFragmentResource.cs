// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a reusable Azure API Management policy fragment.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, FragmentName = {FragmentName}")]
public class AzureApiManagementPolicyFragmentResource(
    string name,
    string fragmentName,
    string value,
    string? description,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical policy-fragment identifier in API Management.
    /// </summary>
    public string FragmentName { get; } = fragmentName;

    /// <summary>
    /// Gets the XML policy statements contained by the fragment.
    /// </summary>
    public string Value { get; } = value;

    /// <summary>
    /// Gets the optional policy-fragment description.
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;
}
