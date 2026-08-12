// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Front Door resource in the distributed application model.
/// </summary>
/// <remarks>
/// Azure Front Door is a global, scalable entry point that uses the Microsoft global edge network to create
/// fast, secure, and widely scalable web applications. It provides load balancing, SSL offloading,
/// and application acceleration for your web applications.
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
public class AzureFrontDoorResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the endpoint URL output reference for a specific origin by its resource name.
    /// </summary>
    /// <param name="originResourceName">The name of the origin resource (as specified in the Aspire application model).</param>
    /// <returns>A <see cref="BicepOutputReference"/> for the Front Door endpoint URL serving that origin.</returns>
    /// <remarks>
    /// The output name follows the pattern <c>{normalizedOriginName}_endpointUrl</c>.
    /// For example, if the origin resource is named "api", the output is <c>api_endpointUrl</c>.
    /// </remarks>
    public BicepOutputReference GetEndpointUrl(string originResourceName)
    {
        var normalizedName = Infrastructure.NormalizeBicepIdentifier(originResourceName);
        return new($"{normalizedName}_endpointUrl", this);
    }
}
