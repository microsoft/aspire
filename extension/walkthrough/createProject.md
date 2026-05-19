# Create your first Aspire app

[Create a new Aspire project](command:aspire-vscode.new) to scaffold a new Aspire application from a starter template.
The starter template gives you:

- An **AppHost** that orchestrates your services, connections, and startup order
- A sample **API service** with health checks
- A **web frontend** that references the API

**The AppHost** is the heart of your app. It defines your application topology in code across supported stacks.

### C# AppHost

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.AspireApp_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.AspireApp_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
```

### TypeScript AppHost

```typescript
import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const api = await builder
    .addNodeApp("api", "./api", "src/index.ts")
    .withHttpEndpoint({ env: "PORT" })
    .withHttpHealthCheck({ path: "/health" });

await builder
    .addViteApp("frontend", "./frontend")
    .withExternalHttpEndpoints()
    .withReference(api)
    .waitFor(api);

await builder.build().run();
```

| Concept | What it does |
|---|---|
| Register a resource | Adds a service, app, container, database, or other resource to the AppHost |
| Reference a resource | Connects one resource to another so connection details flow through the app model |
| Wait for a resource | Controls startup order so dependencies are ready before dependents start |
| Add a health check | Monitors whether a service is ready and healthy |

Your application topology is defined in code, making it easy to understand, modify, and version control. [Learn more on aspire.dev](https://aspire.dev/get-started/first-app/)
