// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Provides well-known Azure service tags that can be used as source or destination address prefixes
/// in network security group rules.
/// </summary>
/// <remarks>
/// <para>
/// Service tags represent a group of IP address prefixes from a given Azure service. Microsoft manages the
/// address prefixes encompassed by each tag and automatically updates them as addresses change.
/// </para>
/// <para>
/// These tags can be used with the <c>from</c> and <c>to</c> parameters of methods such as
/// <see cref="AzureVirtualNetworkExtensions.AllowInbound"/>, <see cref="AzureVirtualNetworkExtensions.DenyInbound"/>,
/// <see cref="AzureVirtualNetworkExtensions.AllowOutbound"/>, <see cref="AzureVirtualNetworkExtensions.DenyOutbound"/>,
/// or with the <see cref="AzureSecurityRule.SourceAddressPrefix"/> and <see cref="AzureSecurityRule.DestinationAddressPrefix"/> properties.
/// </para>
/// </remarks>
/// <example>
/// Use service tags when configuring network security rules:
/// <code>
/// var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
///     .AllowInbound(port: "443", from: AzureServiceTags.AzureLoadBalancer, protocol: SecurityRuleProtocol.Tcp)
///     .DenyInbound(from: AzureServiceTags.Internet);
/// </code>
/// </example>
public static class AzureServiceTags
{
    /// <summary>
    /// Represents the Internet address space, including all publicly routable IP addresses.
    /// </summary>
    public const string Internet = nameof(Internet);

    /// <summary>
    /// Represents the address space for the virtual network, including all connected address spaces,
    /// all connected on-premises address spaces, and peered virtual networks.
    /// </summary>
    public const string VirtualNetwork = nameof(VirtualNetwork);

    /// <summary>
    /// Represents the Azure infrastructure load balancer. This tag is commonly used to allow
    /// health probe traffic from Azure.
    /// </summary>
    public const string AzureLoadBalancer = nameof(AzureLoadBalancer);

    /// <summary>
    /// Represents Azure Traffic Manager probe IP addresses.
    /// </summary>
    public const string AzureTrafficManager = nameof(AzureTrafficManager);

    /// <summary>
    /// Represents the Azure Storage service. This tag does not include specific Storage accounts;
    /// it covers all Azure Storage IP addresses.
    /// </summary>
    public const string Storage = nameof(Storage);

    /// <summary>
    /// Represents Azure SQL Database, Azure Database for MySQL, Azure Database for PostgreSQL,
    /// Azure Database for MariaDB, and Azure Synapse Analytics.
    /// </summary>
    public const string Sql = nameof(Sql);

    /// <summary>
    /// Represents Azure Cosmos DB service addresses.
    /// </summary>
    public const string AzureCosmosDB = nameof(AzureCosmosDB);

    /// <summary>
    /// Represents Azure Key Vault service addresses.
    /// </summary>
    public const string AzureKeyVault = nameof(AzureKeyVault);

    /// <summary>
    /// Represents Azure Event Hubs service addresses.
    /// </summary>
    public const string EventHub = nameof(EventHub);

    /// <summary>
    /// Represents Azure Service Bus service addresses.
    /// </summary>
    public const string ServiceBus = nameof(ServiceBus);

    /// <summary>
    /// Represents Azure Container Registry service addresses.
    /// </summary>
    public const string AzureContainerRegistry = nameof(AzureContainerRegistry);

    /// <summary>
    /// Represents Azure App Service and Azure Functions service addresses.
    /// </summary>
    public const string AppService = nameof(AppService);

    /// <summary>
    /// Represents Microsoft Entra ID (formerly Azure Active Directory) service addresses.
    /// </summary>
    public const string AzureActiveDirectory = nameof(AzureActiveDirectory);

    /// <summary>
    /// Represents Azure Monitor service addresses, including Log Analytics, Application Insights,
    /// and Azure Monitor metrics.
    /// </summary>
    public const string AzureMonitor = nameof(AzureMonitor);

    /// <summary>
    /// Represents the Gateway Manager service, used for VPN Gateway and Application Gateway management traffic.
    /// </summary>
    public const string GatewayManager = nameof(GatewayManager);
}
