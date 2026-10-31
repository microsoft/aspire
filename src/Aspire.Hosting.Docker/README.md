# Docker hosting integration

Provides publishing extensions to Aspire for Docker Compose.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Docker` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Docker
aspire add Aspire.Hosting.Dotnet
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

The dashboard container included in the published Compose file opts out of dashboard usage and error telemetry by default (`ASPIRE_DASHBOARD_TELEMETRY_OPTOUT=true`). This does not affect the application's OpenTelemetry data sent to the dashboard.

Browser-origin endpoint properties use overridable deployment inputs because a Compose service address does not identify its public browser address.
For example, `frontend.GetEndpoint("http").BrowserOrigins.First` reads `Parameters:frontend-http-BrowserOrigin` when preparing deployment.
The `BrowserOrigins` and `BrowserOriginsJson` inputs accept the requested comma-separated and JSON representations respectively; local URL customization is unchanged.
External HTTP endpoints with an explicit published port default to `http://localhost:<published-port>` (or HTTPS for an HTTPS endpoint), not the service's container port.
Endpoints without a known published port require an explicit input.
Generated input names include a stable suffix for hyphenated endpoint names so different resource/endpoint combinations cannot share an input.

### Volumes

Use an environment variable so projects and executables can use a local Aspire store directory while Docker Compose mounts the published named volume:

**C#**

```csharp
builder.AddDotnetProject("api", "../Api/Api.csproj")
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
