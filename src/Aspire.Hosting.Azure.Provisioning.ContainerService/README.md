# Azure Provisioning Kubernetes hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Kubernetes Service (AKS) infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.Kubernetes`; the `ContainerService` suffix follows the underlying `Azure.Provisioning.ContainerService` SDK.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.ContainerService` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.ContainerService
```

## Usage example

Then, in a TypeScript AppHost, customize the managed cluster created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const kubernetes = await builder.addAzureKubernetesEnvironment("aks");

await kubernetes.configureInfrastructure(async infrastructure => {
    const cluster = await infrastructure.getContainerServiceManagedCluster();
    const tags = await cluster.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

`ContainerServiceManagedCluster` is the selected infrastructure root. Compatible models from that SDK assembly are also projected, not every member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression. The overlay configures Azure infrastructure; it does not replace the hosting integration's Helm deployment or local run behavior.

`CustomCATrustCertificates` is explicitly excluded because `BicepList<BinaryData>` has no type-safe ATS element representation.

To customize supporting registry, network, DNS, and workspace resources, opt into `Aspire.Hosting.Azure.Provisioning.ContainerRegistry`, `.Network`, `.PrivateDns`, and `.OperationalInsights` as needed. Shared secret infrastructure uses `.KeyVault`. The Kubernetes hosting dependencies do not automatically opt into those SDK proxies.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.Kubernetes/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/aks/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
