// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An annotation that indicates a resource is the target of a private endpoint.
/// </summary>
/// <remarks>
/// When this annotation is present, the annotated resource should be configured to deny public network access.
/// </remarks>
/// <param name="privateEndpointResource">The private endpoint resource associated with the annotated Azure resource.</param>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/dotnet/aspire/diagnostics#{0}")]
public sealed class PrivateEndpointTargetAnnotation(AzureProvisioningResource privateEndpointResource) : IResourceAnnotation
{
    /// <summary>
    /// Gets the private endpoint resource associated with the annotated Azure resource.
    /// </summary>
    public AzureProvisioningResource PrivateEndpointResource { get; } = privateEndpointResource ?? throw new ArgumentNullException(nameof(privateEndpointResource));
}
