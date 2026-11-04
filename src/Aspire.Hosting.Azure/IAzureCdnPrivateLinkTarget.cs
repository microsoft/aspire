// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a compute environment that an Azure CDN profile, such as Azure Front Door, can reach over Private Link.
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface on a compute environment that is itself the Azure resource a CDN profile's managed private
/// endpoint connects to, such as an Azure Container Apps environment. When
/// <c>WithAzureFrontDoorPrivateLink</c> connects an Azure Front Door to the environment, every Front Door origin that runs in
/// the environment becomes a Private Link origin that targets <see cref="Id"/>.
/// </para>
/// <para>
/// The connecting profile adds a <see cref="PrivateEndpointTargetAnnotation"/> to the environment, as for a private endpoint to any
/// other Azure resource. The implementation should react to that annotation by disabling public network access. The
/// outputs that <see cref="Id"/> and <see cref="Location"/> refer to must always exist.
/// </para>
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public interface IAzureCdnPrivateLinkTarget : IComputeEnvironmentResource
{
    /// <summary>
    /// Gets the "id" output reference from the Azure resource that the CDN profile connects to.
    /// </summary>
    BicepOutputReference Id { get; }

    /// <summary>
    /// Gets the output reference for the Azure region of the Azure resource that the CDN profile connects to.
    /// </summary>
    /// <remarks>
    /// The output must be the programmatic region name of the resource, in lowercase without spaces, such as
    /// <c>westus2</c>, not a display name such as <c>West US 2</c>. The CDN profile creates its private endpoint in this
    /// region, or in a nearby supported region when the CDN doesn't offer Private Link in this one.
    /// </remarks>
    BicepOutputReference Location { get; }

    /// <summary>
    /// Gets the sub-resource (group ID) of the Azure resource that the CDN profile connects to, such as
    /// <c>managedEnvironments</c> for an Azure Container Apps environment.
    /// </summary>
    string PrivateLinkGroupId { get; }
}
