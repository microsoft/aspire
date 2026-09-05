# Azure Provisioning proxy generator

> [!WARNING]
> This package is experimental. The generated proxy APIs emit `ASPIREAZUREPROVISIONING001`, and the source-generator extension contract emits `ASPIREEXPORT018`.

This analyzer package generates bounded polyglot proxies for selected Azure Provisioning SDK assemblies.

Reference the package as a private analyzer dependency, then opt in root types with assembly attributes:

```csharp
using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.KeyVault;

[assembly: GenerateAspireProvisioningProxy(
    typeof(KeyVaultService),
    IncludeContainingAssemblyTypes = true)]
```

When `IncludeContainingAssemblyTypes` is enabled, the generator projects every compatible public class and read-only struct in the root type's assembly. The selected root still controls the no-argument infrastructure lookup, while other provisionable resources receive identifier-based lookup and creation methods. Generated proxies use the shared types from `Aspire.Hosting.Azure.Provisioning` for Bicep values, expressions, and provisionable resources.

The generated opt-in attribute is marked with `AspireExportProviderAttribute`, the general extension point used by the Aspire integration analyzer to recognize source-generated export coverage. Other infrastructure generators can use the same contract without adding provider-specific behavior to the core analyzer.

## Feedback & contributing

https://github.com/microsoft/aspire
