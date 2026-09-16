# Azure Provisioning hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to compose Bicep values and expressions in polyglot Aspire AppHosts that reference an opt-in Azure Provisioning integration.

Resource-specific provisioning integrations reference this package for the shared expression runtime. Each opt-in provisioning package still projects its own Azure Provisioning model proxies, including common models such as user-assigned identities.

Use the generated Bicep factories to compose deployment-time values from literals, resource properties, operators, functions, and interpolated strings. These values can be assigned to generated properties backed by `BicepValue<T>`.

Obtain the factory from the infrastructure callback. For example, the Key Vault provisioning integration can assign a computed integer from a generated TypeScript SDK:

```typescript
import { BinaryBicepOperator, createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const vault = await builder.addAzureKeyVault("vault");

await vault.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getKeyVaultService();
    const properties = await service.properties.get();
    const bicep = infrastructure.bicep();
    const value = bicep.binary(
        bicep.integer(20),
        BinaryBicepOperator.Add,
        bicep.integer(10));

    await properties.softDeleteRetentionInDays.set(value);
});
```

The factory supports literals, common Bicep functions, member and index access, unary and binary operators, conditional expressions, and interpolated string construction. The integration also exposes Bicep parameters, variables, outputs, and user-assigned identities used across multiple Azure Provisioning SDKs.

## Hosting-to-provisioning inventory

These opt-in integrations are bounded SDK configuration overlays, not new resource lifecycle integrations. Each SDK proxy references its corresponding existing hosting project, this shared runtime, and the private generator analyzer. Adding a hosting integration does not automatically opt into its SDK proxies.

The tables below map Azure hosting integrations to their provisioning SDK proxies and describe the base hosting, shared runtime, and generator projects.

### Hosting integrations

Hosting suffixes below expand to `Aspire.Hosting.Azure.{suffix}`. SDK and proxy suffixes expand to `Azure.Provisioning.{suffix}` and `Aspire.Hosting.Azure.Provisioning.{suffix}`, respectively. The primary column maps the hosting resource to its SDK overlay; the last column lists supporting overlays needed **only when customizing those SDK models**, not additional requirements for ordinary hosting API use.

All hosting integrations also use base Azure provisioning support, including shared Key Vault infrastructure where needed. This common dependency is not repeated in every row. A companion resource can have its own infrastructure callback; installing its proxy does not make it appear in the primary resource's callback.

| Hosting integration | Primary SDK / opt-in proxy | Supporting SDK / proxy dependencies and scope |
| --- | --- | --- |
| [AppConfiguration](../Aspire.Hosting.Azure.AppConfiguration/README.md) | `AppConfiguration` | App Configuration store. |
| [AppContainers](../Aspire.Hosting.Azure.AppContainers/) | `AppContainers` | `ContainerRegistry`, `OperationalInsights`, `Storage`, `KeyVault`; separately modeled networking uses `Network` and, for private DNS, `PrivateDns`. |
| [AppService](../Aspire.Hosting.Azure.AppService/README.md) | `AppService` | `ContainerRegistry`, `ApplicationInsights`, `OperationalInsights`; separately modeled subnets use `Network` and private DNS uses `PrivateDns`. |
| [ApplicationInsights](../Aspire.Hosting.Azure.ApplicationInsights/README.md) | `ApplicationInsights` | `OperationalInsights` for the workspace. |
| [CognitiveServices](../Aspire.Hosting.Azure.CognitiveServices/README.md) | `CognitiveServices` | Includes Azure OpenAI accounts and deployments. |
| [ConnectorNamespace](../Aspire.Hosting.Azure.ConnectorNamespace/README.md) | Base provisioning support | Uses internal custom `ConnectorGateway` models for `Microsoft.Web/connectorGateways` and its children. These are not a standalone Azure Provisioning SDK and are not projected by an SDK proxy. |
| [ContainerRegistry](../Aspire.Hosting.Azure.ContainerRegistry/) | `ContainerRegistry` | Registry and task models. |
| [CosmosDB](../Aspire.Hosting.Azure.CosmosDB/README.md) | `CosmosDB` | `KeyVault` for supporting secrets. |
| [EventHubs](../Aspire.Hosting.Azure.EventHubs/README.md) | `EventHubs` | `Storage` for storage-backed scenarios. |
| [FrontDoor](../Aspire.Hosting.Azure.FrontDoor/README.md) | `Cdn` | Front Door profiles and routing models are in the CDN SDK. |
| [Functions](../Aspire.Hosting.Azure.Functions/README.md) | `Storage` (shared) | Host storage uses the Storage integration; deployment to Container Apps uses `AppContainers` and its dependencies. No Functions SDK proxy is needed. |
| [KeyVault](../Aspire.Hosting.Azure.KeyVault/README.md) | `KeyVault` | Vaults and secrets. |
| [Kubernetes](../Aspire.Hosting.Azure.Kubernetes/README.md) | `ContainerService` | `ContainerRegistry`, `Network`, `PrivateDns`, `OperationalInsights`; Kubernetes/Helm deployment is not a separate Azure Provisioning SDK. |
| [Kusto](../Aspire.Hosting.Azure.Kusto/README.md) | `Kusto` | Cluster and database models. |
| [Network](../Aspire.Hosting.Azure.Network/README.md) | `Network`, `PrivateDns` | Private DNS is a separate SDK; private endpoint targets use their service's own proxy. |
| [OperationalInsights](../Aspire.Hosting.Azure.OperationalInsights/README.md) | `OperationalInsights` | Log Analytics workspace. |
| [PostgreSQL](../Aspire.Hosting.Azure.PostgreSQL/README.md) | `PostgreSql` | `KeyVault` for password authentication; SDK spelling differs from the hosting suffix. |
| [Redis](../Aspire.Hosting.Azure.Redis/README.md) | `Redis`, `RedisEnterprise` | `Redis` covers legacy Azure Cache for Redis; `RedisEnterprise` covers Azure Managed Redis. `KeyVault` supports access-key authentication. |
| [Sandboxes](../Aspire.Hosting.Azure.Sandboxes/README.md) | `ContainerRegistry`; base support | Uses internal custom `SandboxGroup` provisioning and sandbox endpoint handling, not a standalone Azure SDK assembly. These internal models are not projected by an SDK proxy. |
| [Search](../Aspire.Hosting.Azure.Search/README.md) | `Search` | Search service. |
| [ServiceBus](../Aspire.Hosting.Azure.ServiceBus/README.md) | `ServiceBus` | Namespace and messaging entities. |
| [SignalR](../Aspire.Hosting.Azure.SignalR/README.md) | `SignalR` | SignalR service. |
| [Sql](../Aspire.Hosting.Azure.Sql/README.md) | `Sql` | `Storage`, `Network`, and `PrivateDns` for supporting storage and networking scenarios. |
| [Storage](../Aspire.Hosting.Azure.Storage/README.md) | `Storage` | Accounts, blob, queue, table, and file models; also reused by Functions. |
| [WebPubSub](../Aspire.Hosting.Azure.WebPubSub/README.md) | `WebPubSub` | Service and hub models. |

### SDK proxy projects

Each linked suffix is an exact `Aspire.Hosting.Azure.Provisioning.{suffix}` project and uses `Azure.Provisioning.{suffix}`. "No-argument lookup roots" lists the types marked as infrastructure roots in `AtsTypeMappings.cs`, not an exhaustive supported-model list. Compatible types from the selected SDK assembly are also considered; other resources use identifier-based lookup.

| SDK proxy | Referenced hosting suffix | No-argument lookup roots |
| --- | --- | --- |
| [AppConfiguration](../Aspire.Hosting.Azure.Provisioning.AppConfiguration/README.md) | `AppConfiguration` | `AppConfigurationStore` |
| [AppContainers](../Aspire.Hosting.Azure.Provisioning.AppContainers/README.md) | `AppContainers` | `ContainerAppManagedEnvironment`; apps and jobs use identifier-based lookup. |
| [AppService](../Aspire.Hosting.Azure.Provisioning.AppService/README.md) | `AppService` | None; plans and sites use identifier-based lookup. |
| [ApplicationInsights](../Aspire.Hosting.Azure.Provisioning.ApplicationInsights/README.md) | `ApplicationInsights` | `ApplicationInsightsComponent` |
| [Cdn](../Aspire.Hosting.Azure.Provisioning.Cdn/README.md) | `FrontDoor` | `CdnProfile` |
| [CognitiveServices](../Aspire.Hosting.Azure.Provisioning.CognitiveServices/README.md) | `CognitiveServices` | `CognitiveServicesAccount` |
| [ContainerRegistry](../Aspire.Hosting.Azure.Provisioning.ContainerRegistry/README.md) | `ContainerRegistry` | `ContainerRegistryService` |
| [ContainerService](../Aspire.Hosting.Azure.Provisioning.ContainerService/README.md) | `Kubernetes` | `ContainerServiceManagedCluster` |
| [CosmosDB](../Aspire.Hosting.Azure.Provisioning.CosmosDB/README.md) | `CosmosDB` | `CosmosDBAccount` |
| [EventHubs](../Aspire.Hosting.Azure.Provisioning.EventHubs/README.md) | `EventHubs` | `EventHubsNamespace` |
| [KeyVault](../Aspire.Hosting.Azure.Provisioning.KeyVault/README.md) | `KeyVault` | `KeyVaultService` |
| [Kusto](../Aspire.Hosting.Azure.Provisioning.Kusto/README.md) | `Kusto` | `KustoCluster` |
| [Network](../Aspire.Hosting.Azure.Provisioning.Network/README.md) | `Network` | `VirtualNetwork`, `NetworkSecurityGroup`, `NatGateway`, `PublicIPAddress`, `PrivateEndpoint`, `NetworkSecurityPerimeter` |
| [OperationalInsights](../Aspire.Hosting.Azure.Provisioning.OperationalInsights/README.md) | `OperationalInsights` | `OperationalInsightsWorkspace` |
| [PostgreSql](../Aspire.Hosting.Azure.Provisioning.PostgreSql/README.md) | `PostgreSQL` | `PostgreSqlFlexibleServer` |
| [PrivateDns](../Aspire.Hosting.Azure.Provisioning.PrivateDns/README.md) | `Network` | `PrivateDnsZone` |
| [Redis](../Aspire.Hosting.Azure.Provisioning.Redis/README.md) | `Redis` | `Azure.Provisioning.Redis.RedisResource` |
| [RedisEnterprise](../Aspire.Hosting.Azure.Provisioning.RedisEnterprise/README.md) | `Redis` | `RedisEnterpriseCluster` |
| [Search](../Aspire.Hosting.Azure.Provisioning.Search/README.md) | `Search` | `SearchService` |
| [ServiceBus](../Aspire.Hosting.Azure.Provisioning.ServiceBus/README.md) | `ServiceBus` | `ServiceBusNamespace` |
| [SignalR](../Aspire.Hosting.Azure.Provisioning.SignalR/README.md) | `SignalR` | `SignalRService` |
| [Sql](../Aspire.Hosting.Azure.Provisioning.Sql/README.md) | `Sql` | `SqlServer` |
| [Storage](../Aspire.Hosting.Azure.Provisioning.Storage/README.md) | `Storage` | `StorageAccount` |
| [WebPubSub](../Aspire.Hosting.Azure.Provisioning.WebPubSub/README.md) | `WebPubSub` | `WebPubSubService` |

### Base, shared runtime, and generator

| Project | Role | SDK coverage |
| --- | --- | --- |
| `Aspire.Hosting.Azure` | Base hosting and Azure infrastructure support | `Azure.Provisioning` and `Azure.Provisioning.KeyVault`; shared infrastructure does not require another service SDK or proxy package. |
| `Aspire.Hosting.Azure.Provisioning` | Shared experimental runtime | Bicep expressions, parameters, variables, outputs, and shared identity support. Service-specific Key Vault customization uses the `.KeyVault` proxy. |
| `Aspire.Hosting.Azure.Provisioning.Generators` | Private build-time analyzer | Generates bounded proxies from explicit SDK selections; not an AppHost service integration or an additional Azure SDK. |

### Lookup and compatibility boundaries

No-argument root lookups match the callback's Aspire resource Bicep identifier, not the Azure resource's physical name. Use identifier-based lookup when those identifiers differ: for example, App Service plans use `<environment identifier>_asplan`. Lookups are confined to the current infrastructure callback, not a global application inventory.

The scope is intentionally bounded. `IncludeContainingAssemblyTypes` does not promise that every SDK member is supported. Unsupported members are explicitly excluded in `AtsTypeMappings.cs`, such as opaque `BinaryData` payloads. The overlays document their exclusions: ContainerService's `CustomCATrustCertificates` and Network/Redis `AdditionalProperties`. These are unavailable through the proxy, not silently converted or dropped values.

IP address lists (`BicepList<IPAddress>`) support IPv4/IPv6 strings and Bicep value handles. Strings are parsed into SDK IP address literals, with invalid input reported as an error. Element getters return Bicep value handles that retain literal, expression, reference, and security metadata and can be reassigned to another IP address collection. SDK read-only output restrictions still apply.

## Creating a provisioning proxy integration

Integration authors can create an opt-in package for another Azure Provisioning SDK without changing Aspire's general-purpose integration analyzer. Reference `Aspire.Hosting.Azure.Provisioning` for the shared runtime proxies and reference `Aspire.Hosting.Azure.Provisioning.Generators` as a private analyzer dependency. Select the Azure SDK assembly and the resource that represents the current Aspire infrastructure:

```csharp
using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.KeyVault;

[assembly: GenerateAspireProvisioningProxy(
    typeof(KeyVaultService),
    IncludeContainingAssemblyTypes = true)]
```

The generator projects compatible public classes and read-only structs in the selected type's assembly while keeping the exported polyglot surface bounded to that SDK package. A selected resource receives a no-argument infrastructure lookup when its Bicep identifier matches the callback's hosting resource identifier. Set `IsInfrastructureRoot = false` when those identifiers differ; identifier-based lookup and creation methods remain available where supported. Use `ExcludedMemberNames` for unsupported members rather than assuming assembly-wide selection makes every property representable.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/
* https://learn.microsoft.com/azure/azure-resource-manager/bicep/

## Feedback & contributing

https://github.com/microsoft/aspire
