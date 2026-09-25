// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Well-known Azure service identifiers for subnet service delegation.
/// </summary>
/// <remarks>
/// Delegating a subnet grants an Azure service permission to create service-specific resources in that
/// subnet. These constants provide discoverable, strongly-named values for the services Aspire integrates
/// with, so callers don't have to remember or duplicate the underlying resource provider strings when
/// delegating a subnet. Each value is an Azure resource provider path; see
/// <see href="https://learn.microsoft.com/azure/virtual-network/subnet-delegation-overview">Subnet delegation</see>.
/// </remarks>
/// <example>
/// This example delegates a subnet to Azure Container Instances using a well-known value:
/// <code>
/// var vnet = builder.AddAzureVirtualNetwork("vnet");
/// var subnet = vnet.AddSubnet("aci-subnet", "10.0.0.0/23")
///     .WithServiceDelegation(AzureSubnetServiceDelegations.ContainerInstances);
/// </code>
/// </example>
public static class AzureSubnetServiceDelegations
{
    /// <summary>
    /// Delegation for Azure Container Instances (ACI), which allows container groups to be deployed into the subnet.
    /// </summary>
    [AspireValue("AzureSubnetServiceDelegations")]
    public const string ContainerInstances = "Microsoft.ContainerInstance/containerGroups";

    /// <summary>
    /// Delegation for Azure Container Apps environments.
    /// </summary>
    [AspireValue("AzureSubnetServiceDelegations")]
    public const string ContainerAppEnvironments = "Microsoft.App/environments";

    /// <summary>
    /// Delegation for Azure App Service environments.
    /// </summary>
    [AspireValue("AzureSubnetServiceDelegations")]
    public const string AppServiceEnvironments = "Microsoft.Web/serverFarms";

    /// <summary>
    /// Delegation for Azure Application Gateway for Containers.
    /// </summary>
    [AspireValue("AzureSubnetServiceDelegations")]
    public const string ApplicationGatewayForContainers = "Microsoft.ServiceNetworking/trafficControllers";
}
