# Aspire.Hosting.Azure.WebPubSub library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Web PubSub.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure Web PubSub Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.WebPubSub
```

## Usage example

In the _AppHost.cs_ file of `AppHost`, add a WebPubSub connection and consume the connection using the following methods:

```csharp
var wps = builder.AddAzureWebPubSub("wps1");

var web = builder.AddProject<Projects.WebPubSubWeb>("webfrontend")
                       .WithReference(wps);
```

## Connection Properties

When you reference an Azure Web PubSub resource using `WithReference`, the following connection properties are made available to the consuming project:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The HTTPS endpoint for the Web PubSub service, typically `https://<resource-name>.webpubsub.azure.com/` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `wps1` becomes `WPS1_URI`.

## Additional documentation

* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire
