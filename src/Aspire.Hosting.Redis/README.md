# Redis hosting integration

Use this integration to model, configure, and orchestrate a Redis resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Redis` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Redis
```

## Usage example

In the AppHost, add a Redis resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var redis = builder.AddRedis("redis");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(redis);
```

**TypeScript**

```typescript
const redis = await builder.addRedis("redis");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(redis);
```

## REPL

Call `WithRepl()` to opt into a **REPL** command on the Redis resource in the dashboard:

```csharp
builder.AddRedis("redis").WithRepl();
```

```typescript
await builder.addRedis("redis").withRepl();
```

REPL access is disabled by default and is available only in run mode. Enable it only for trusted dashboard
users: the shell uses the resource's configured credentials and is not read-only. Sharing the dashboard
through a tunnel, Codespaces, or VS Code remote development also exposes this capability to users who can
execute resource commands.

Use `quit` before closing the terminal tab to end the session cleanly. Closing the tab alone can
leave `redis-cli` running inside the container. Stopping the container also ends any remaining REPL processes.

When the container is running, this opens an authenticated `redis-cli` session in the terminal dock.
Use `quit` to exit. The session runs inside the container using Docker (or the configured Podman runtime);
no local Redis client is required. Passwords are passed through environment variables, not command-line
arguments. When TLS is enabled, the in-container REPL uses the non-TLS port over loopback.

## Connection Properties

When you reference a Redis resource using `WithReference`, the following connection properties are made available to the consuming project:

### Redis

The Redis resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Redis server |
| `Port` | The port number the Redis server is listening on |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `redis://:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `cache` becomes `CACHE_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/caching/redis/redis-host/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Redis is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._
