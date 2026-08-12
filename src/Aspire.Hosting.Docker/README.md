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

```shell
aspire publish -o docker-compose-artifacts
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/compute/docker/

## Feedback & contributing

https://github.com/microsoft/aspire
