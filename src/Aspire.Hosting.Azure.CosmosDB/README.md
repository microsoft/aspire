# Azure Cosmos DB hosting integration

Use this integration to model, configure, and orchestrate Azure CosmosDB in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.CosmosDB` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.CosmosDB
```

## Configure Azure Provisioning for local development

Adding Azure resources to the AppHost model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via AppHost configuration. From your AppHost directory, set these values with `aspire secret set`:

```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

In the AppHost, add a Cosmos DB connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var cosmosdb = builder.AddAzureCosmosDB("cdb").AddCosmosDatabase("cosmosdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(cosmosdb);
```

**TypeScript**

```typescript
const cosmosdb = await builder.addAzureCosmosDB("cdb").addCosmosDatabase("cosmosdb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(cosmosdb);
```

## Connection Properties

When you reference Azure Cosmos DB resources using `WithReference`, the following connection properties are made available to the consuming project:

### Cosmos DB account

The Cosmos DB account resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The account endpoint URI for the Cosmos DB account, with the format `https://mycosmosaccount.documents.azure.com:443/` |
| `AccountKey` | The account key for the Cosmos DB account (only available for emulator and access key authentication) |
| `ConnectionString` | **Emulator or access key authentication only.** A full connection string (includes account key for emulator; access key secret when access key auth is enabled). |

### Cosmos DB database

The Cosmos DB database resource inherits all properties from its parent Cosmos DB account and adds:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The name of the database |

### Cosmos DB container

The Cosmos DB container resource inherits all properties from its parent Cosmos DB database and adds:

| Property Name | Description |
|---------------|-------------|
| `ContainerName` | The name of the container |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-cosmos-db/azure-cosmos-db-host/
* https://learn.microsoft.com/azure/cosmos-db/nosql/

## Feedback & contributing

https://github.com/microsoft/aspire
