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
await api.withVolume("/data", "data", "DATA_PATH");
```

In run mode, projects and executables receive a workload-scoped directory through `DATA_PATH`. Containers receive `/data` and use a local container volume. Named storage is preserved independently of resource lifetime: session resources stop with the AppHost and reuse their storage on the next run, while persistent resources can keep the compute instance alive. In the generated Compose service, all compute resource types receive `/data` and a named volume mounted at that path.

> [!NOTE]
> Docker Compose creates the named volume owned by `root`, so a service published as a non-root image cannot write to the mount without extra configuration. Aspire does not yet generate that configuration, so today the service has to run as a user that can write to the volume. Docker only seeds a named volume's contents and ownership from the image when the volume is empty, so pre-creating the target directory in your image fixes only the first run and has no effect once the volume holds data — which is exactly the reuse case described above. Tracked by [#19422](https://github.com/microsoft/aspire/issues/19422).

```shell
aspire publish -o docker-compose-artifacts
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/compute/docker/

## Feedback & contributing

https://github.com/microsoft/aspire
