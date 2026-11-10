# Microsoft Graph provisioning hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Microsoft Entra ID registration infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

The [Entra ID integration's prerequisites](../Aspire.Hosting.Azure.EntraId/README.md#getting-started) apply.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.EntraId` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.EntraId
```

## Usage example

Then, in a TypeScript AppHost, customize the Graph application and service principal created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const serviceReference = await builder.addParameter("service-reference");
const entra = await builder.addEntraIdApplication("entra");

await entra.configureInfrastructure(async infrastructure => {
    const application = await infrastructure.getMicrosoftGraphApplicationByIdentifier("application");
    await application.serviceManagementReference.set(infrastructure.bicep().parameter(serviceReference));
    const tags = await application.tags.get();
    await tags.add("department:engineering");

    const principal = await infrastructure.getMicrosoftGraphServicePrincipalByIdentifier("servicePrincipal");
    const principalTags = await principal.tags.get();
    await principalTags.add("department:engineering");
});
```

Graph declarations use the Bicep identifiers `application` and `servicePrincipal`, not the Aspire resource name, so look them up by identifier. Properties accept literals or shared Bicep values. Graph tags are strings; add to the service principal's tags rather than replacing them. See [Customize the generated infrastructure](../Aspire.Hosting.Azure.EntraId/README.md#customize-the-generated-infrastructure) for the C# API and details.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* [Shared Azure Provisioning expression APIs](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://learn.microsoft.com/graph/templates/bicep/reference/applications
* https://learn.microsoft.com/graph/templates/bicep/reference/serviceprincipals

## Feedback & contributing

https://github.com/microsoft/aspire
