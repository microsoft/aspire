# Azure Provisioning Cognitive Services hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure AI services infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.CognitiveServices` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.CognitiveServices
```

## Usage example

Then, in a TypeScript AppHost, customize the Azure OpenAI account created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const openai = await builder.addAzureOpenAI("openai");

await openai.configureInfrastructure(async infrastructure => {
    const account = await infrastructure.getCognitiveServicesAccount();
    const tags = await account.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Cognitive Services API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/ai-services/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
