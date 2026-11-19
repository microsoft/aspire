// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Requests that the Azure deployment target of a compute resource emit the URL of an endpoint as
/// it is actually deployed (for example the ingress FQDN Azure assigns to a container app) as a
/// Bicep output.
/// </summary>
/// <remarks>
/// Azure resources that need the deployed address of a compute resource, such as a Web PubSub hub
/// whose event handlers call back into an app, add this annotation to the compute resource while the
/// application model is being built. Deployment targets that implement
/// <see cref="IAzureDeployedEndpointUrlProvider"/> then emit an output named
/// <see cref="GetOutputName(string)"/> for each requested endpoint.
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

/// <summary>
/// Implemented by Azure deployment targets that can emit the deployed URL of a compute resource's
/// endpoints as Bicep outputs.
/// </summary>
/// <remarks>
/// Implementations emit an output named <see cref="AzureDeployedEndpointUrlAnnotation.GetOutputName(string)"/>
/// for every endpoint requested through an <see cref="AzureDeployedEndpointUrlAnnotation"/> on the
/// target compute resource. Consumers reference that output with a <see cref="BicepOutputReference"/>,
/// which makes their provisioning wait until the compute resource has been deployed.
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IAzureDeployedEndpointUrlProvider : IAzureResource
{
    /// <summary>
    /// Gets a reference to the deployed URL of an endpoint.
    /// </summary>
    /// <param name="endpointReference">The endpoint, which must have been requested with an <see cref="AzureDeployedEndpointUrlAnnotation"/>.</param>
    /// <returns>A reference to the Bicep output containing the deployed URL.</returns>
    BicepOutputReference GetDeployedEndpointUrl(EndpointReference endpointReference);
}
