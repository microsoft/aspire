# Aspire.Hosting.Azure.Network library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Virtual Networks, Subnets, NAT Gateways, Public IP Addresses, Network Security Groups, Network Security Perimeters, and Private Endpoints.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure Virtual Network Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Network
```

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. Set these values in user secrets in order to allow resources to be configured
automatically.

```json
{
    "Azure": {
      "SubscriptionId": "<your subscription id>",
      "ResourceGroupPrefix": "<prefix for the resource group>",
      "Location": "<azure location>"
    }
}
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage examples

### Adding a Virtual Network

In the _AppHost.cs_ file of `AppHost`, add a Virtual Network using the following method:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet");
```

By default, the virtual network will use the address prefix `10.0.0.0/16`. You can specify a custom address prefix:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.1.0.0/16");
```

### Adding Subnets

You can add subnets to your virtual network:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet");
var subnet = vnet.AddSubnet("subnet", "10.0.1.0/24");
```

### Adding NAT Gateways

A NAT Gateway provides outbound internet connectivity with deterministic public IP addresses:

```csharp
var natGateway = builder.AddNatGateway("nat");

var vnet = builder.AddAzureVirtualNetwork("vnet");
var subnet = vnet.AddSubnet("aca-subnet", "10.0.0.0/23")
    .WithNatGateway(natGateway);
```

By default, a Public IP Address is automatically created. You can provide an explicit one for full control:

```csharp
var pip = builder.AddPublicIPAddress("nat-pip");
var natGateway = builder.AddNatGateway("nat")
    .WithPublicIPAddress(pip);
```

Use `ConfigureInfrastructure` for advanced settings like idle timeout or availability zones.

### Adding Network Security Groups

Add security rules to control traffic flow on subnets using shorthand methods:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet");
var subnet = vnet.AddSubnet("web", "10.0.1.0/24")
    .AllowInbound(port: "443", from: AzureServiceTags.AzureLoadBalancer, protocol: SecurityRuleProtocol.Tcp)
    .DenyInbound(from: AzureServiceTags.Internet);
```

An NSG is automatically created when shorthand methods are used. Priority auto-increments (100, 200, 300...) and rule names are auto-generated.

For full control, create an explicit NSG with `AzureSecurityRule` objects:

```csharp
var nsg = vnet.AddNetworkSecurityGroup("web-nsg")
    .WithSecurityRule(new AzureSecurityRule
    {
        Name = "allow-https",
        Priority = 100,
        Direction = SecurityRuleDirection.Inbound,
        Access = SecurityRuleAccess.Allow,
        Protocol = SecurityRuleProtocol.Tcp,
        DestinationPortRange = "443"
    });

var subnet = vnet.AddSubnet("web-subnet", "10.0.1.0/24")
    .WithNetworkSecurityGroup(nsg);
```

A single NSG can be shared across multiple subnets.

### Adding Network Security Perimeters

A Network Security Perimeter (NSP) groups PaaS resources into a logical security boundary. Resources within the perimeter can communicate with each other, while public access is restricted by access rules:

```csharp
var nsp = builder.AddNetworkSecurityPerimeter("my-nsp")
    .WithAccessRule(new AzureNspAccessRule
    {
        Name = "allow-my-ip",
        Direction = NetworkSecurityPerimeterAccessRuleDirection.Inbound,
        AddressPrefixes = { "203.0.113.0/24" }
    });

var storage = builder.AddAzureStorage("storage");
var keyVault = builder.AddAzureKeyVault("kv");

storage.WithNetworkSecurityPerimeter(nsp);
keyVault.WithNetworkSecurityPerimeter(nsp);
```

Associations use `Enforced` access mode by default, which blocks non-compliant public traffic. Use `Learning` mode to log violations without blocking, which is useful when onboarding resources to identify required access rules:

```csharp
// Learning mode — logs violations without blocking traffic
storage.WithNetworkSecurityPerimeter(nsp, NetworkSecurityPerimeterAssociationAccessMode.Learning);
```

### Adding Private Endpoints

Create a private endpoint to securely connect to Azure resources over a private network:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet");
var peSubnet = vnet.AddSubnet("private-endpoints", "10.0.2.0/24");

var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("blobs");

// Add a private endpoint for the blob storage
peSubnet.AddPrivateEndpoint(blobs);
```

When you add a private endpoint to an Azure resource:

1. A Private DNS Zone is automatically created for the service (e.g., `privatelink.blob.core.windows.net`)
2. A Virtual Network Link connects the DNS zone to your VNet
3. A DNS Zone Group is created on the private endpoint for automatic DNS registration
4. The target resource is automatically configured to deny public network access

To override the automatic network lockdown, use `ConfigureInfrastructure`:

```csharp
storage.ConfigureInfrastructure(infra =>
{
    var storageAccount = infra.GetProvisionableResources()
        .OfType<StorageAccount>()
        .Single();
    storageAccount.PublicNetworkAccess = StoragePublicNetworkAccess.Enabled;
});
```

## Additional documentation

* https://learn.microsoft.com/azure/virtual-network/
* https://learn.microsoft.com/azure/nat-gateway/
* https://learn.microsoft.com/azure/private-link/
* https://learn.microsoft.com/azure/private-link/network-security-perimeter-concepts

## Feedback & contributing

https://github.com/microsoft/aspire
