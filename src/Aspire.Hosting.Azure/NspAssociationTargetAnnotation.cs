// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An annotation that indicates a resource is associated with a Network Security Perimeter.
/// </summary>
/// <remarks>
/// When this annotation is present, the annotated resource should be configured to set
/// <c>publicNetworkAccess</c> to <c>"SecuredByPerimeter"</c>.
/// </remarks>
/// <param name="nspResource">The Network Security Perimeter resource that the annotated resource is associated with.</param>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/dotnet/aspire/diagnostics#{0}")]
public sealed class NspAssociationTargetAnnotation(AzureProvisioningResource nspResource) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Network Security Perimeter resource associated with the annotated Azure resource.
    /// </summary>
    public AzureProvisioningResource NspResource { get; } = nspResource ?? throw new ArgumentNullException(nameof(nspResource));
}
