// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure API Management named value.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, NamedValueName = {NamedValueName}")]
public class AzureApiManagementNamedValueResource(
    string name,
    string namedValueName,
    string displayName,
    object value,
    bool secret,
    IReadOnlyList<string> tags,
    AzureApiManagementResource parent)
    : Resource(name), IResourceWithParent<AzureApiManagementResource>
{
    /// <summary>
    /// Gets the physical named-value identifier in API Management.
    /// </summary>
    public string NamedValueName { get; } = namedValueName;

    /// <summary>
    /// Gets the named-value display name used in policy expressions.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets whether the named value contains secret material.
    /// </summary>
    public bool Secret { get; } = secret;

    /// <summary>
    /// Gets the tags associated with the named value.
    /// </summary>
    public IReadOnlyList<string> Tags { get; } = tags;

    /// <summary>
    /// Gets the parent API Management service.
    /// </summary>
    public AzureApiManagementResource Parent { get; } = parent;

    internal object Value { get; } = value;
}
