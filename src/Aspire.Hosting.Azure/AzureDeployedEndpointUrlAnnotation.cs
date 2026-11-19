// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Associates a compute resource's endpoint with the Bicep output containing its deployed URL.
/// </summary>
/// <remarks>
/// Add this annotation to the compute resource before deployment targets are prepared.
/// If <see cref="Url"/> is unset, the compute environment supplies its deployed endpoint output.
/// An ingress resource can instead supply its own output. Consumers must read the annotation after
/// deployment targets are prepared, but before discovering provisioning dependencies or generating Bicep.
/// There must be only one annotation for each endpoint name.
/// <code>
/// var callbackUrl = new AzureDeployedEndpointUrlAnnotation("https")
/// {
///     Url = frontDoor.Resource.GetEndpointUrl(api.Resource.Name)
/// };
/// api.WithAnnotation(callbackUrl);
/// </code>
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureDeployedEndpointUrlAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureDeployedEndpointUrlAnnotation"/> class.
    /// </summary>
    /// <param name="endpointName">The name of the endpoint whose deployed URL is requested.</param>
    public AzureDeployedEndpointUrlAnnotation(string endpointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        EndpointName = endpointName;
    }

    /// <summary>
    /// Gets the name of the endpoint whose deployed URL is requested.
    /// </summary>
    public string EndpointName { get; }

    /// <summary>
    /// Gets or sets the output containing the deployed base URL, without a trailing slash.
    /// </summary>
    /// <remarks>
    /// The output can belong to a different resource, such as Azure Front Door. Its owning resource
    /// determines the consumer's provisioning dependency. Leave this unset to request the compute
    /// environment's default output. Ingress resources must resolve their backend addresses separately
    /// rather than using this public-facing URL.
    /// </remarks>
    public BicepOutputReference? Url { get; set; }

    /// <summary>
    /// Gets the name of the Bicep output that contains the deployed URL of an endpoint.
    /// </summary>
    /// <param name="endpointName">The name of the endpoint.</param>
    /// <returns>The Bicep output name.</returns>
    public static string GetOutputName(string endpointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        return $"{Infrastructure.NormalizeBicepIdentifier(endpointName)}_url";
    }
}
