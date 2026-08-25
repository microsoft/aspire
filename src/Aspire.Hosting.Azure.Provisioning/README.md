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

## Creating a provisioning proxy integration

Integration authors can create an opt-in package for another Azure Provisioning SDK without changing Aspire's general-purpose integration analyzer. Reference `Aspire.Hosting.Azure.Provisioning` for the shared runtime proxies and reference `Aspire.Hosting.Azure.Provisioning.Generators` as a private analyzer dependency. Select the Azure SDK roots that should be projected:

```csharp
using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.KeyVault;

[assembly: GenerateAspireProvisioningProxy(typeof(KeyVaultService))]
```

The generator follows the public Azure Provisioning types reachable from those roots while keeping the exported polyglot surface bounded to the selected SDK.

## Additional documentation

* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/
* https://learn.microsoft.com/azure/azure-resource-manager/bicep/

## Feedback & contributing

https://github.com/microsoft/aspire
