// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Network;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Public IP Address resources to the application model.
/// </summary>
public static class AzurePublicIPAddressExtensions
{
    /// <summary>
    /// Adds an Azure Public IP Address resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the Azure Public IP Address resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzurePublicIPAddressResource}"/>.</returns>
    /// <remarks>
    /// The Public IP Address is created with Standard SKU and Static allocation by default.
    /// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/>
    /// to customize properties such as DNS labels, availability zones, or IP version.
    /// </remarks>
    /// <example>
    /// This example creates a Public IP Address:
    /// <code>
    /// var pip = builder.AddPublicIPAddress("my-pip");
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an Azure Public IP Address resource to the application model.")]
    public static IResourceBuilder<AzurePublicIPAddressResource> AddPublicIPAddress(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzurePublicIPAddressResource(name, ConfigurePublicIPAddress);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    private static void ConfigurePublicIPAddress(AzureResourceInfrastructure infra)
    {
        var pip = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infra,
            (identifier, name) =>
            {
                var resource = PublicIPAddress.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                return new PublicIPAddress(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Sku = new PublicIPAddressSku()
                    {
                        Name = PublicIPAddressSkuName.Standard,
                    },
                    PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                };
            });

        infra.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = pip.Id
        });

        infra.Add(new ProvisioningOutput("name", typeof(string))
        {
            Value = pip.Name
        });

        infra.Add(new ProvisioningOutput("ipAddress", typeof(string))
        {
            Value = pip.IPAddress
        });
    }
}
