# Orleans hosting integration

Use this integration to model, configure, and orchestrate an Orleans cluster in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Orleans` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Orleans
```

## Usage example

In the AppHost, add an Orleans cluster resource and reference it from other resources with either C# or TypeScript:

**C#**

```csharp
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var clusteringTable = storage.AddTables("clustering");
var grainStorage = storage.AddBlobs("grainstate");

var orleans = builder.AddOrleans("my-app")
                     .WithClustering(clusteringTable)
                     .WithGrainStorage("Default", grainStorage);

builder.AddProject<Projects.OrleansServer>("silo")
       .WithReference(orleans);

builder.AddProject<Projects.OrleansClient>("frontend")
       .WithReference(orleans.AsClient());
```

**TypeScript**

```typescript
const storage = await builder.addAzureStorage("storage").runAsEmulator();
const clusteringTable = await storage.addTables("clustering");
const grainStorage = await storage.addBlobs("grainstate");

const orleans = await builder.addOrleans("my-app")
                     .withClustering(clusteringTable)
                     .withGrainStorage("Default", grainStorage);

await builder.addNodeApp("silo", "../orleans-silo", "server.js")
    .withReference(orleans);

await builder.addNodeApp("frontend", "../orleans-frontend", "server.js")
    .withReference(orleans.asClient());
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/frameworks/orleans/
https://learn.microsoft.com/dotnet/orleans/

## Feedback & contributing

https://github.com/microsoft/aspire
