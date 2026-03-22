# Create your first Aspire app

[Create a new Aspire project](command:aspire-vscode.new) to scaffold a new solution from a starter template.
The starter template gives you:
- An **apphost** that orchestrates your services, connections, and startup order
- A sample **API service** with health checks
- A **web frontend** that references the API

**The apphost** is the heart of your app — it defines everything in code:

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var apiService = builder.AddProject<Projects.AspireApp_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");
builder.AddProject<Projects.AspireApp_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService)    // web app can call the API
    .WaitFor(apiService);         // start API first
builder.Build().Run();
```

| Method | What it does |
|---|---|
| `AddProject` | Registers a service |
| `WithReference` | Connects services |
| `WaitFor` | Controls startup order |
| `WithHttpHealthCheck` | Monitors health |

Your application topology is defined in code, making it easy to understand, modify, and version control. [Learn more on aspire.dev](https://aspire.dev/get-started/first-app/)
