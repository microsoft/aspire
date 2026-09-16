# Azure Provisioning Key Vault hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Key Vault infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.KeyVault` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.KeyVault
```

## Usage example

Then, in a TypeScript AppHost, customize the Key Vault service created by Aspire:

```typescript
import { BinaryBicepOperator, KeyVaultSkuName, createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const vault = await builder.addAzureKeyVault("vault");

await vault.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getKeyVaultService();
    const properties = await service.properties.get();
    const sku = await properties.sku.get();
    const bicep = infrastructure.bicep();
    const retention = bicep.binary(
        bicep.integer(20),
        BinaryBicepOperator.Add,
        bicep.integer(10));

    await properties.enablePurgeProtection.set(true);
    await properties.softDeleteRetentionInDays.set(retention);
    await sku.name.set(KeyVaultSkuName.Premium);
});
```

The integration generates an ATS-compatible proxy over the Azure Provisioning Key Vault SDK. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep value. The shared value preserves literals, expressions, resource references, and secure-value metadata when assigned to the underlying provisioning object.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/key-vault/general/overview
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
