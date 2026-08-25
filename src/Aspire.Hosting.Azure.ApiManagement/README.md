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

`AddApi` creates an API, an APIM backend, and a wildcard operation that forwards requests to the deployed endpoint of the target resource. APIs require an APIM subscription key by default. Set `subscriptionRequired: false` only when the API should be callable without one.

API Management resources are automatically omitted during `aspire run`. Azure compute environments do not materialize their public endpoints in run mode, and a cloud-hosted APIM instance cannot reach a backend running on localhost. Use `aspire deploy` to provision APIM and exercise its routing; no execution-mode guard is required around the APIM resources.

## OpenAI backend pools

Use `AddOpenAIApi` to load balance an OpenAI-compatible API across Azure OpenAI or Microsoft Foundry deployments:

```csharp
var primaryFoundry = builder.AddFoundry("foundry-primary");
var primary = primaryFoundry.AddDeployment(
    "chat-primary",
    FoundryModel.OpenAI.Gpt5Mini);

var secondaryFoundry = builder.AddFoundry("foundry-secondary");
var secondary = secondaryFoundry.AddDeployment(
    "chat-secondary",
    FoundryModel.OpenAI.Gpt5Mini);

apim.AddOpenAIApi("openai-api", path: "openai")
    .WithFoundryBackend(primary, priority: 1, weight: 3)
    .WithFoundryBackend(secondary, priority: 1, weight: 1);
```

The generated APIM backend pool uses weighted routing between healthy members at the same priority. A lower priority number is preferred; members at the next priority are used when every member in a preferred group has an open circuit. Each backend has a circuit breaker that opens on HTTP 429 and honors the Azure OpenAI `Retry-After` response header.

The API authenticates with the APIM system-assigned managed identity. Aspire grants that identity the Cognitive Services OpenAI User role on Azure OpenAI accounts and the Cognitive Services User role on Foundry accounts. The deploying principal needs `Microsoft.Authorization/roleAssignments/write` permission on those accounts, such as through the User Access Administrator or Owner role. Backend URLs include each physical deployment name, so a request such as:

```text
POST https://<gateway>.azure-api.net/openai/chat/completions?api-version=<version>
```

is forwarded to the selected account at:

```text
POST https://<account>/openai/deployments/<deployment>/chat/completions?api-version=<version>
```

## Policies

Append policy statements to the generated inbound section at the service, API, or operation scope:

```csharp
var api = apim.AddApi("catalog-api", catalog, "catalog")
    .WithInboundPolicy("""
        <rate-limit calls="100" renewal-period="60" />
        """);
```

Use `WithPolicy` when a complete APIM policy document is required. APIM replaces the complete policy at that scope; replacing an API policy also replaces Aspire's generated backend-routing statement. `WithPolicy` and `WithInboundPolicy` cannot be combined at the same scope because doing so would silently discard one configuration.

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

### Private Container Apps backends

Place the Container Apps environment and APIM in separate subnets of the same virtual network. Configure the Container Apps environment with an internal load balancer and APIM with classic external VNet injection:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet");
var containerAppsSubnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
var apimSubnet = vnet.AddSubnet("apim-subnet", "10.0.2.0/24");

var environment = builder.AddAzureContainerAppEnvironment("env")
    .WithDelegatedSubnet(containerAppsSubnet)
    .WithInternalLoadBalancer(vnet);

var catalog = builder.AddProject<Projects.Catalog>("catalog")
    .WithComputeEnvironment(environment)
    .WithExternalHttpEndpoints();

apim.WithClassicVirtualNetwork(
    apimSubnet,
    AzureApiManagementVirtualNetworkMode.External);

apim.AddApi("catalog-api", catalog, "catalog");
```

`WithInternalLoadBalancer` gives the Container Apps environment a private VIP and creates a private DNS zone, wildcard A record, and virtual-network link for its generated default domain. Although `WithExternalHttpEndpoints` enables ingress outside the Container Apps environment, the environment itself has no public endpoint; APIM reaches the app over the virtual network and is the public gateway.

Classic APIM VNet injection requires an NSG on the APIM subnet. Allow inbound TCP 3443 from the `ApiManagement` service tag, TCP 6390 from `AzureLoadBalancer`, and TCP 443 from `Internet` when the APIM gateway remains public.

Inbound private endpoints are not yet supported by this integration. APIM must first be provisioned with public access enabled, then receive its private endpoint, and finally be updated to disable public access. The integration rejects this configuration until it can model that multi-phase lifecycle safely.

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
