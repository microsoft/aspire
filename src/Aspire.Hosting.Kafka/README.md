# Aspire.Hosting.Kafka library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Kafka resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Kafka Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Kafka
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Kafka resource and consume the connection using the following methods:

```csharp
var kafka = builder.AddKafka("messaging");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(kafka);
```

## Connection Properties

When you reference a Kafka resource using `WithReference`, the following connection properties are made available to the consuming project:

### Kafka server

The Kafka server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The host-facing Kafka listener hostname or IP address |
| `Port` | The host-facing Kafka listener port |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `messaging` becomes `MESSAGING_URI`.

## Additional documentation

* https://learn.microsoft.com/dotnet/aspire/messaging/kafka-component

## Feedback & contributing

https://github.com/dotnet/aspire
