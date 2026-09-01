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

The compute-targeted `AddApi` overload creates an API, an APIM backend, and catch-all operations for supported HTTP methods that forward requests to the deployed endpoint of the target resource. APIs require an APIM subscription key by default. Set `subscriptionRequired: false` only when the API should be callable without one.

API Management resources are automatically omitted during `aspire run`. Azure compute environments do not materialize their public endpoints in run mode, and a cloud-hosted APIM instance cannot reach a backend running on localhost. Use `aspire deploy` to provision APIM and exercise its routing; no execution-mode guard is required around the APIM resources.

## OpenAPI import

Import the OpenAPI document exposed by an Aspire compute resource:

```csharp
apim.AddApi("catalog-api", catalog, "catalog")
    .WithOpenApiEndpoint("/openapi/v1.json");
```

Aspire resolves the target's deployed external HTTP or HTTPS endpoint and gives APIM a link to the document. The endpoint must be publicly reachable from the APIM control plane during deployment. The default document path is `/openapi/v1.json`.

For private backends or documents generated before deployment, import a file relative to the AppHost directory:

```csharp
apim.AddApi("catalog-api", catalog, "catalog")
    .WithOpenApiDocument("../Catalog/openapi.json");
```

JSON and YAML OpenAPI documents are inferred from the file extension. Pass `AzureApiManagementOpenApiFormat.SwaggerJson` explicitly for Swagger 2.0 documents. Imported operations replace the generated catch-all proxy operations; operations added with `AddOperation` are still provisioned in addition to the imported operations.

## Existing API Management services

Adopt an existing service while letting Aspire manage the APIs and other child resources declared in the AppHost:

```csharp
var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
}).PublishAsExisting("shared-apim", resourceGroup: "shared-infrastructure");

apim.AddApi("catalog-api", catalog, "catalog");
```

Aspire treats the existing service itself as read-only. It can manage declared APIs, operations, backends, pools, policies, fragments, products, subscriptions, named values, and diagnostics, but it rejects service-level virtual-network, private-endpoint, custom-domain, and managed-identity mutations.

When managed backends authenticate with the existing service's system identity, confirm that the identity is already enabled:

```csharp
apim.WithExistingSystemAssignedIdentity();
```

This confirmation does not modify the service. Aspire creates separate role-assignment modules whenever API Management or a backend or Key Vault target uses an explicitly scoped existing Azure resource in another resource group or subscription. Diagnostics configured on an existing APIM service still require the service and Application Insights resource to be in the same deployment resource group.

## Backends and backend pools

Backends and pools are first-class resources that can be reused by APIs. The specialized Foundry adapter configures the deployment URL, managed-identity authentication, the required Cognitive Services role assignment, and an HTTP 429 circuit breaker:

```csharp
var primaryFoundry = builder.AddFoundry("foundry-primary");
var primary = primaryFoundry.AddDeployment(
    "chat-primary",
    FoundryModel.OpenAI.Gpt5Mini);

var secondaryFoundry = builder.AddFoundry("foundry-secondary");
var secondary = secondaryFoundry.AddDeployment(
    "chat-secondary",
    FoundryModel.OpenAI.Gpt5Mini);

var primaryBackend = apim.AddFoundryBackend("primary-backend", primary);
var secondaryBackend = apim.AddFoundryBackend("secondary-backend", secondary);

var pool = apim.AddBackendPool("openai-pool")
    .WithBackend(primaryBackend, priority: 1, weight: 3)
    .WithBackend(secondaryBackend, priority: 1, weight: 1);

apim.AddOpenAIApi("openai-api", path: "openai")
    .WithBackend(pool);
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

The same pool model works with other services. For example, two Blob Storage services can share a pool:

```csharp
var primaryBlobs = builder.AddAzureStorage("storage-primary").AddBlobs("blobs-primary");
var secondaryBlobs = builder.AddAzureStorage("storage-secondary").AddBlobs("blobs-secondary");

var primaryBackend = apim.AddBlobStorageBackend("blob-primary", primaryBlobs);
var secondaryBackend = apim.AddBlobStorageBackend("blob-secondary", secondaryBlobs);
var pool = apim.AddBackendPool("blob-pool")
    .WithBackend(primaryBackend, weight: 3)
    .WithBackend(secondaryBackend);

apim.AddApi("blob-api", path: "blobs")
    .WithBackend(pool)
    .WithInboundPolicy(
        """<set-header name="x-ms-version" exists-action="override"><value>2023-11-03</value></set-header>""");
```

`AddBlobStorageBackend` grants the APIM managed identity the Storage Blob Data Reader role and configures the Storage authentication audience.

Use `AddBackend` as the low-level escape hatch for other services or APIM backend features:

```csharp
var backend = apim.AddBackend(
    "custom-backend",
    ReferenceExpression.Create($"https://backend.example.com"),
    new AzureApiManagementBackendOptions
    {
        Protocol = AzureApiManagementBackendProtocol.Soap,
        ManagedIdentityResource = "api://backend",
        ValidateCertificateName = false,
        CircuitBreaker = new AzureApiManagementCircuitBreakerOptions
        {
            Name = "serverErrors",
            FailureCount = 3,
            FailureIntervalSeconds = 30,
            TripDurationSeconds = 60,
            StatusCodeRanges = [new(500, 599)],
        },
    });

apim.AddApi("custom-api", path: "custom")
    .WithBackend(backend);
```

Generic backends do not infer Azure role assignments because the required role is service-specific. Configure those permissions on the target resource separately when using managed identity.

The same generic backend and pool APIs are available in TypeScript:

```typescript
import { refExpr } from "./.aspire/modules/aspire.mjs";

const primary = await apim.addBackend(
    "primary",
    refExpr`https://primary.example.com`,
    {
        options: {
            managedIdentityResource: "api://backend",
        },
    });
const secondary = await apim.addBackend(
    "secondary",
    refExpr`https://secondary.example.com`,
    {
        options: {
            managedIdentityResource: "api://backend",
        },
    });

const pool = await apim.addBackendPool("backend-pool");
await pool.addBackendPoolMember(primary, { weight: 3 });
await pool.addBackendPoolMember(secondary);

const api = await apim.addApiWithoutTarget("pooled-api", "pooled");
await api.withApiBackendPool(pool);
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

### Policy fragments

Define shared policy statements once and include them at the service, API, or operation scope:

```csharp
var standardHeaders = apim.AddPolicyFragment(
    "standard-headers",
    """
    <set-header name="x-powered-by" exists-action="override">
      <value>Aspire</value>
    </set-header>
    """);

apim.WithInboundPolicyFragment(standardHeaders);
catalogApi.WithInboundPolicyFragment(standardHeaders);
```

Aspire creates the required `<fragment>` document and ensures policies are deployed after their referenced fragments.

## Products and subscriptions

Group APIs into a product and optionally provision a product-scoped subscription:

```csharp
apim.AddProduct("catalog-product", "Catalog", new()
    {
        Description = "Catalog APIs",
        SubscriptionRequired = true,
        ApprovalRequired = false,
    })
    .WithApi(catalogApi)
    .AddSubscription("catalog-client", "Catalog client");
```

API Management generates the primary and secondary subscription keys. Aspire intentionally does not expose those keys as ordinary deployment outputs because they are secrets.

## Named values

Named values can contain a non-secret literal, a secret Aspire parameter, or a Key Vault secret reference:

```csharp
apim.AddNamedValue("region", "westus3");

var apiKey = builder.AddParameter("upstream-api-key", secret: true);
apim.AddSecretNamedValue("upstream-api-key-value", apiKey, displayName: "UpstreamApiKey");

var vault = builder.AddAzureKeyVault("vault");
apim.AddKeyVaultNamedValue(
    "shared-secret",
    vault.GetSecret("shared-secret"),
    displayName: "SharedSecret");
```

Key Vault references stay late-bound and use versionless secret URIs so APIM can refresh rotated values. Aspire creates a user-assigned identity before the APIM service, grants it the Key Vault Secrets User role, and configures APIM to use that identity. This avoids the first-deployment dependency cycle that occurs when a new APIM service tries to use its not-yet-created system identity.

## Application Insights diagnostics

Enable service-wide diagnostics or override the settings for an individual API:

```csharp
var insights = builder.AddAzureApplicationInsights("insights");

apim.WithApplicationInsights(insights, new()
{
    SamplingPercentage = 25,
});

catalogApi.WithApplicationInsights(insights, new()
{
    SamplingPercentage = 100,
    Verbosity = AzureApiManagementDiagnosticVerbosity.Error,
});
```

The default diagnostic uses W3C correlation, always logs errors, emits metrics, and does not capture request or response bodies.

## Custom domains and certificates

Bind a custom APIM endpoint to a PFX certificate stored as a Key Vault secret:

```csharp
var certificateVault = builder.AddAzureKeyVault("certificates")
    .PublishAsExisting("contoso-certificates", resourceGroup: null);

apim.WithCustomDomain(
    "api.contoso.com",
    certificateVault.GetSecret("gateway-certificate"),
    AzureApiManagementHostnameType.Proxy,
    defaultSslBinding: true);
```

The certificate secret URI is versionless so APIM can automatically refresh renewed certificates. Aspire creates a user-assigned identity before the APIM service, grants it the Key Vault Certificate User role, and configures the hostname to use that identity. The Consumption SKU supports one custom domain for the gateway endpoint only. Key Vaults that require APIM's system identity through the trusted-services firewall exception need a staged deployment and are not currently supported by this API.

## TypeScript service configuration

Products, named values, policy fragments, diagnostics, and custom domains are available to polyglot AppHosts:

```typescript
const insights = await builder.addAzureApplicationInsights("insights");
const vault = await builder.addAzureKeyVault("vault");
const apim = await builder.addAzureApiManagement("apim", {
    publisherEmail: "api-owners@example.com",
});

const fragment = await apim.addPolicyFragment(
    "standard-headers",
    '<set-header name="x-powered-by" exists-action="override"><value>Aspire</value></set-header>');
await apim.withInboundPolicyFragment(fragment);
await apim.withApplicationInsights(insights);
await apim.addKeyVaultNamedValue(
    "shared-secret",
    vault,
    "shared-secret");

const product = await apim.addProduct("catalog-product", "Catalog");
await product.withApi(catalogApi);
await product.addSubscription("catalog-client", "Catalog client");
```

## SKUs and networking

Configure the tier and capacity with `AzureApiManagementOptions`. The integration supports Consumption, Developer, Basic, Basic v2, Standard, Standard v2, Premium, and Premium v2 capacity validation.

Classic Developer and Premium VNet injection can use an existing undelegated Aspire subnet:

```csharp
var subnet = builder.AddAzureVirtualNetwork("vnet")
    .AddSubnet("apim-subnet", "10.0.0.0/24");

apim.WithClassicVirtualNetwork(
    subnet,
    AzureApiManagementVirtualNetworkMode.Internal);
```

Standard v2 and Premium v2 outbound integration automatically delegates a dedicated subnet to `Microsoft.Web/serverFarms`:

```csharp
var subnet = builder.AddAzureVirtualNetwork("vnet")
    .AddSubnet("apim-subnet", "10.0.0.0/24");

apim.WithVirtualNetworkIntegration(subnet);
```

Premium v2 injection provides private inbound and outbound access and automatically delegates its subnet to `Microsoft.Web/hostingEnvironments`:

```csharp
var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    Sku = AzureApiManagementSku.PremiumV2,
});

apim.WithVirtualNetworkInjection(subnet);
```

Both v2 configurations require a dedicated `/27` or larger subnet. Aspire creates an NSG when the subnet does not already have one and adds the required outbound HTTPS rule for the `AzureKeyVault` service tag. Premium v2 injection must be selected when the service is created. Configure private DNS for the injected gateway hostname as described in the Azure API Management Premium v2 injection documentation.

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

Create an inbound private endpoint for the APIM gateway with the Azure networking integration:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet");
var privateEndpointSubnet = vnet.AddSubnet("private-endpoint-subnet", "10.0.0.0/24");

privateEndpointSubnet.AddPrivateEndpoint(apim);
```

Aspire creates the service with its default public access, provisions the private endpoint and `privatelink.azure-api.net` DNS zone, waits for the connection to be approved, and then runs a staged update that disables public network access. During `aspire deploy`, Aspire uses the deployment credential to send a narrow ARM PATCH that changes only `publicNetworkAccess`. Published standalone Bicep uses a least-privilege deployment script for the same update because Bicep cannot express PATCH operations. Azure Deployment Scripts require a supporting storage account with shared-key authentication, so subscriptions that prohibit storage shared keys must use `aspire deploy` rather than deploying the published Bicep directly for this scenario. Private endpoints cannot be combined with classic or Premium v2 VNet injection. Consumption and Basic v2 do not support private endpoints.

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
* https://learn.microsoft.com/azure/api-management/integrate-vnet-outbound
* https://learn.microsoft.com/azure/api-management/inject-vnet-v2
* https://learn.microsoft.com/azure/api-management/private-endpoint

## Feedback & contributing

https://github.com/microsoft/aspire
