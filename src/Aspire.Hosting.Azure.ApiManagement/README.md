# Azure API Management hosting integration

Use this integration to model, configure, and orchestrate an Azure API Management gateway in an Aspire solution.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.ApiManagement` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.ApiManagement
```

## Usage example

Then, in the AppHost, add an Azure API Management resource and route a public path to an Aspire compute resource with either C# or TypeScript:

**C#**

```csharp
var catalog = builder.AddProject<Projects.Catalog>("catalog")
    .WithExternalHttpEndpoints();

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
});

apim.AddApi("catalog-api", catalog, "catalog");
```

**TypeScript**

```typescript
const catalog = await builder.addNodeApp("catalog", "../catalog", "server.js")
    .withExternalHttpEndpoints();

const apim = await builder.addAzureApiManagement("apim", {
    publisherEmail: "api-owners@example.com",
});

await apim.addApi("catalog-api", catalog, "catalog");
```

`AddApi` creates an API, an APIM backend, and a wildcard operation that forwards requests to the deployed endpoint of the target resource.

API Management resources are automatically omitted during `aspire run`. Azure compute environments do not materialize their public endpoints in run mode, and a cloud-hosted APIM instance cannot reach a backend running on localhost. Use `aspire deploy` to provision APIM and exercise its routing; no execution-mode guard is required around the APIM resources.

## Policies

Append policy statements to the generated inbound section at the service, API, or operation scope:

```csharp
var api = apim.AddApi("catalog-api", catalog, "catalog")
    .WithInboundPolicy("""
        <rate-limit calls="100" renewal-period="60" />
        """);
```

Use `WithPolicy` when a complete APIM policy document is required. APIM replaces the complete policy at that scope; replacing an API policy also replaces Aspire's generated backend-routing statement.

## SKUs and networking

Configure the tier and capacity with `AzureApiManagementOptions`. The integration supports Consumption, Developer, Basic, Basic v2, Standard, Standard v2, Premium, and Premium v2 capacity validation.

Classic Developer and Premium VNet injection can use an existing Aspire subnet:

```csharp
var subnet = builder.AddAzureVirtualNetwork("vnet")
    .AddSubnet("apim-subnet", "10.0.0.0/24");

apim.WithClassicVirtualNetwork(
    subnet,
    AzureApiManagementVirtualNetworkMode.Internal);
```

The subnet must be undelegated and should have the APIM-required network security rules. Standard v2 outbound integration and Premium v2 injection use different subnet delegation and lifecycle models and are not configured by `WithClassicVirtualNetwork`.

API Management also supports inbound private endpoints through the Azure network integration:

```csharp
var privateEndpointSubnet = builder.AddAzureVirtualNetwork("vnet")
    .AddSubnet("private-endpoints", "10.0.1.0/24");

privateEndpointSubnet.AddPrivateEndpoint(apim);
```

## Configure Azure provisioning for local development

Adding Azure resources to the Aspire application model automatically enables development-time provisioning. From your AppHost directory, set the required values:

```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
```

See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/api-management/
* https://learn.microsoft.com/azure/api-management/api-management-howto-policies
* https://learn.microsoft.com/azure/api-management/virtual-network-concepts

## Feedback & contributing

https://github.com/microsoft/aspire
