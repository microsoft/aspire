# Aspire.Hosting.RabbitMQ library

Provides extension methods and resource definitions for an Aspire AppHost to configure a RabbitMQ resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire RabbitMQ Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.RabbitMQ
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a RabbitMQ resource and consume the connection using the following methods:

```csharp
var rmq = builder.AddRabbitMQ("rmq");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(rmq);
```

## Connection Properties

When you reference a RabbitMQ resource using `WithReference`, the following connection properties are made available to the consuming project:

### RabbitMQ server

The RabbitMQ server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the RabbitMQ server |
| `Port` | The port number the RabbitMQ server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `amqp://{Username}:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/messaging/rabbitmq/

## Feedback & contributing

https://github.com/microsoft/aspire
