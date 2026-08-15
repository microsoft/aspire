# Docker hosting integration

Provides publishing extensions to Aspire for Docker Compose.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Docker` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Docker
```

## Usage example

In the AppHost, add the environment:

**C#**

```csharp
builder.AddDockerComposeEnvironment("compose");
```

**TypeScript**

```typescript
await builder.addDockerComposeEnvironment("compose");
```

### Volumes

Use an environment variable so projects and executables can use a local Aspire store directory while Docker Compose mounts the published named volume:

**C#**

```csharp
builder.AddProject<Projects.Api>("api")
    .WithVolume("data", "/data", env: "DATA_PATH");
```

**TypeScript**

```typescript
const api = await builder.addNodeApp("api", "../api", "server.js");
await api.withVolume("/data", { name: "data", env: "DATA_PATH" });
```

In run mode, projects and executables receive a workload-scoped directory through `DATA_PATH`. Containers receive `/data` and use a local container volume. In the generated Compose service, all compute resource types receive `/data` and a named volume mounted at that path.

```shell
aspire publish -o docker-compose-artifacts
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/compute/docker/

## Feedback & contributing

https://github.com/microsoft/aspire
