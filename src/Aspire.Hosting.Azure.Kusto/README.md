# Azure Data Explorer hosting integration

Use this integration to model, configure, and orchestrate a Kusto resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Kusto` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Kusto
```

## Usage example

In the AppHost, add a Kusto resource and reference it from another resource:

**C#**

```csharp
var db = builder.AddAzureKustoCluster("kusto")
                .RunAsEmulator()
                .AddReadWriteDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addAzureKustoCluster("kusto")
                .runAsEmulator()
                .addReadWriteDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(db);
```

## Connection Properties

When you reference Azure Kusto resources using `WithReference`, the following connection properties are made available to the consuming project:

### Cluster Resource

| Property Name | Description |
|---------------|-------------|
| `Uri`         | The cluster endpoint URI, typically `https://<cluster-name>.<region>.kusto.windows.net/` (or the HTTP endpoint when using the emulator) |

### Database Resource

| Property Name | Description |
|---------------|-------------|
| `Uri`         | The cluster endpoint URI (inherited from parent cluster) |
| `DatabaseName`    | The name of the database |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `mydb` becomes `MYDB_URI`, and the `DatabaseName` property becomes `MYDB_DATABASENAME`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-data-explorer/
* https://learn.microsoft.com/en-us/kusto/
* https://learn.microsoft.com/en-us/kusto/api/
* https://learn.microsoft.com/en-us/azure/data-explorer/kusto-emulator-overview

## Feedback & contributing

https://github.com/microsoft/aspire
