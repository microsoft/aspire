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
/// Implement this interface on a compute environment that can supply the Azure resource a CDN profile's managed private
/// endpoint connects to, such as an Azure Container Apps environment or an individual App Service website. When
/// <c>WithAzureFrontDoorPrivateLink</c> connects an Azure Front Door to the environment, every Front Door origin that runs in
/// the environment becomes a Private Link origin that targets <see cref="GetPrivateLinkResourceId"/>.
/// </para>
/// <para>
/// The connecting profile adds a <see cref="PrivateEndpointTargetAnnotation"/> to the environment, as for a private endpoint to any
/// other Azure resource. The implementation should react to that annotation by disabling public network access. The
/// outputs returned by this interface must exist when Private Link is configured.
/// </para>
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public interface IAzureCdnPrivateLinkTarget : IComputeEnvironmentResource
{
    /// <summary>
    /// Gets the resource ID output reference for the Azure resource that the CDN profile connects to for an origin.
    /// </summary>
    /// <param name="origin">The compute resource added as an origin to the CDN profile.</param>
    /// <returns>The resource ID of the Private Link target, not necessarily the compute environment itself.</returns>
    /// <remarks>
    /// Container Apps origins share their environment's private endpoint. App Service origins each require a private
    /// endpoint to their own website, rather than to the App Service plan.
    /// </remarks>
    BicepOutputReference GetPrivateLinkResourceId(IResource origin);

    /// <summary>
    /// Gets the region output reference for the Azure resource that the CDN profile connects to for an origin.
    /// </summary>
    /// <param name="origin">The compute resource added as an origin to the CDN profile.</param>
    /// <returns>The Azure region of the Private Link target.</returns>
    /// <remarks>
    /// The output must be the programmatic region name of the resource, in lowercase without spaces, such as
    /// <c>westus2</c>, not a display name such as <c>West US 2</c>. The CDN profile creates its private endpoint in this
    /// region, or in a nearby supported region when the CDN doesn't offer Private Link in this one.
    /// </remarks>
    BicepOutputReference GetPrivateLinkLocation(IResource origin);

    /// <summary>
    /// Gets the sub-resource (group ID) of the Azure resource that the CDN profile connects to, such as
    /// <c>managedEnvironments</c> for an Azure Container Apps environment.
    /// </summary>
    string PrivateLinkGroupId { get; }
}
