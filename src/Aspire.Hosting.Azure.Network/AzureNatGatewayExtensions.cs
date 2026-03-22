// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Network;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure NAT Gateway resources to the application model.
/// </summary>
public static class AzureNatGatewayExtensions
{
    /// <summary>
    /// Adds an Azure NAT Gateway resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the Azure NAT Gateway resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureNatGatewayResource}"/>.</returns>
    /// <remarks>
    /// The NAT Gateway is created with Standard SKU. If no Public IP Address is explicitly associated
    /// via <see cref="WithPublicIPAddress"/>, a Public IP Address is automatically created in the
    /// NAT Gateway's bicep module with Standard SKU and Static allocation.
    /// </remarks>
    /// <example>
    /// This example creates a NAT Gateway and associates it with a subnet:
    /// <code>
    /// var natGateway = builder.AddNatGateway("nat");
    ///
    /// var vnet = builder.AddAzureVirtualNetwork("vnet");
    /// var subnet = vnet.AddSubnet("aca-subnet", "10.0.0.0/23")
    ///     .WithNatGateway(natGateway);
    /// </code>
    /// </example>
    [AspireExport("addNatGateway", Description = "Adds an Azure NAT Gateway resource to the application model.")]
    public static IResourceBuilder<AzureNatGatewayResource> AddNatGateway(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureNatGatewayResource(name, ConfigureNatGateway);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Associates an explicit Public IP Address resource with the NAT Gateway.
    /// </summary>
    /// <param name="builder">The NAT Gateway resource builder.</param>
    /// <param name="publicIPAddress">The Public IP Address resource to associate.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureNatGatewayResource}"/> for chaining.</returns>
    /// <remarks>
    /// When an explicit Public IP Address is provided, the NAT Gateway will not auto-create one.
    /// </remarks>
    /// <example>
    /// This example creates a NAT Gateway with an explicit Public IP:
    /// <code>
    /// var pip = builder.AddPublicIPAddress("nat-pip");
    /// var natGateway = builder.AddNatGateway("nat")
    ///     .WithPublicIPAddress(pip);
    /// </code>
    /// </example>
    [AspireExport("withPublicIPAddress", Description = "Associates an Azure Public IP Address resource with an Azure NAT Gateway resource.")]
    public static IResourceBuilder<AzureNatGatewayResource> WithPublicIPAddress(
        this IResourceBuilder<AzureNatGatewayResource> builder,
        IResourceBuilder<AzurePublicIPAddressResource> publicIPAddress)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(publicIPAddress);

        builder.Resource.PublicIPAddresses.Add(publicIPAddress.Resource);
        return builder;
    }

    private static void ConfigureNatGateway(AzureResourceInfrastructure infra)
    {
        var azureResource = (AzureNatGatewayResource)infra.AspireResource;

        var natGw = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infra,
            (identifier, name) =>
            {
                var resource = NatGateway.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                var natGw = new NatGateway(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    SkuName = NatGatewaySkuName.Standard,
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                };

                // If explicit Public IP addresses are provided, reference them via parameters
                if (azureResource.PublicIPAddresses.Count > 0)
                {
                    foreach (var pipResource in azureResource.PublicIPAddresses)
                    {
                        var pipIdParam = pipResource.Id.AsProvisioningParameter(infrastructure);
                        natGw.PublicIPAddresses.Add(new WritableSubResource
                        {
                            Id = pipIdParam
                        });
                    }
                }
                else
                {
                    // Auto-create a Public IP Address inline
                    var pip = new PublicIPAddress($"{infrastructure.AspireResource.GetBicepIdentifier()}_pip")
                    {
                        Sku = new PublicIPAddressSku()
                        {
                            Name = PublicIPAddressSkuName.Standard,
                        },
                        PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    infrastructure.Add(pip);

                    natGw.PublicIPAddresses.Add(new WritableSubResource
                    {
                        Id = pip.Id
                    });
                }

                return natGw;
            });

        infra.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = natGw.Id
        });

        infra.Add(new ProvisioningOutput("name", typeof(string))
        {
            Value = natGw.Name
        });
    }
}
