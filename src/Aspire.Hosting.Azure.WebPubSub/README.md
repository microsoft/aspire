# Azure Web PubSub hosting integration

Use this integration to model, configure, and orchestrate Azure Web PubSub in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription for Azure deployment - [create one for free](https://azure.microsoft.com/free/)
- A container runtime such as Docker or Podman for local emulator use

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.WebPubSub` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.WebPubSub
```

## Usage example

In the AppHost, add a WebPubSub connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var wps = builder.AddAzureWebPubSub("wps1");

var web = builder.AddProject<Projects.WebPubSubWeb>("webfrontend")
                       .WithReference(wps);
```

**TypeScript**

```typescript
const wps = await builder.addAzureWebPubSub("wps1");

const web = await builder.addNodeApp("webfrontend", "../web-frontend", "server.js")
                       .withReference(wps);
```

## Run locally with the emulator

Call `RunAsEmulator` to use `mcr.microsoft.com/webpubsub/emulator:1.0.0-beta.2` during local development. Aspire supplies the HTTP endpoint and access key in the connection string. Publishing continues to provision Azure Web PubSub.

**C#**

```csharp
var wps = builder.AddAzureWebPubSub("wps1").RunAsEmulator();
var chat = wps.AddHub("chat");

var web = builder.AddProject<Projects.WebPubSubWeb>("webfrontend")
                 .WithReference(chat)
                 .WaitFor(wps);

chat.AddEventHandler($"{web.GetEndpoint("http")}/events", systemEvents: ["connected"]);
```

**TypeScript**

```typescript
const wps = await builder.addAzureWebPubSub("wps1").runAsEmulator();
const chat = await wps.addHub("chat");

const web = await builder.addNodeApp("webfrontend", "../web-frontend", "server.js")
                         .withReference(chat)
                         .waitFor(wps);
```

Event handlers configured with `AddEventHandler` also run in the emulator. Use an HTTP endpoint for local callbacks; event handler authentication is not supported by the emulator. JavaScript clients using `@azure/web-pubsub` need `allowInsecureConnection: true` when connecting to its HTTP endpoint.

To select a fixed host port, configure the emulator endpoint:

```csharp
var wps = builder.AddAzureWebPubSub("wps1")
                 .RunAsEmulator(emulator => emulator.WithEndpoint("emulator", endpoint => endpoint.Port = 8080));
```

## Connection Properties

When you reference an Azure Web PubSub resource using `WithReference`, the following connection properties are made available to the consuming project:

### Azure Web PubSub service

The Azure Web PubSub resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The service endpoint, typically `https://<resource-name>.webpubsub.azure.com/`, or the local HTTP endpoint when using the emulator |

### Azure Web PubSub hub

The Azure Web PubSub hub resource inherits all properties from its parent Azure Web PubSub service and adds:

| Property Name | Description |
|---------------|-------------|
| `HubName` | The name of the Web PubSub hub |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `wps1` becomes `WPS1_URI`, and the `HubName` property of a hub called `chat` becomes `CHAT_HUBNAME`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-web-pubsub/azure-web-pubsub-host/
* https://learn.microsoft.com/azure/azure-web-pubsub/overview

## Feedback & contributing

https://github.com/microsoft/aspire
