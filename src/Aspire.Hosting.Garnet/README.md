# Garnet hosting integration

Use this integration to model, configure, and orchestrate a Garnet cache resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Garnet` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Garnet
```

## Usage example

In the AppHost, add a Garnet resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var garnet = builder.AddGarnet("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(garnet);
```

**TypeScript**

```typescript
const garnet = await builder.addGarnet("cache");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(garnet);
```

## Connection Properties

When you reference a Garnet resource using `WithReference`, the following connection properties are made available to the consuming project:

### Garnet

The Garnet resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Garnet server |
| `Port` | The port number the Garnet server is listening on |
| `Password` | The password for authentication (available when a password parameter is configured) |
| `Uri` | The connection URI, with the format `redis://:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `cache` becomes `CACHE_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/caching/garnet/garnet-host/
* https://github.com/microsoft/garnet/blob/main/README.md
* https://microsoft.github.io/garnet/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Garnet MIT License. Copyright (c) Microsoft Corporation.._
