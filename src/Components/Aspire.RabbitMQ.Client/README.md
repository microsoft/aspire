# Aspire.RabbitMQ.Client library

Registers an [IConnection](https://rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.IConnection.html) in the DI container for connecting to a RabbitMQ server. Enables corresponding health check, logging and telemetry.

## Getting started

### Prerequisites

- RabbitMQ server and the server hostname for connecting a client.

### Install the package

Install the Aspire RabbitMQ library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.RabbitMQ.Client
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddRabbitMQClient` extension method to register an `IConnection` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddRabbitMQClient("messaging");
```

You can then retrieve the `IConnection` instance using dependency injection. For example, to retrieve the connection from a Web API controller:

```csharp
private readonly IConnection _connection;

public ProductsController(IConnection connection)
{
    _connection = connection;
}
```

## Configuration

The Aspire RabbitMQ component provides multiple options to configure the connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddRabbitMQClient()`:

```csharp
builder.AddRabbitMQClient("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "amqp://username:password@localhost:5672"
  }
}
```

See the [ConnectionString documentation](https://www.rabbitmq.com/uri-spec.html) for more information on how to format this connection string.

### Use configuration providers

The Aspire RabbitMQ component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `RabbitMQClientSettings` from configuration by using the `Aspire:RabbitMQ:Client` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "RabbitMQ": {
      "Client": {
        "DisableHealthChecks": true
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<RabbitMQClientSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddRabbitMQClient("messaging", settings => settings.DisableHealthChecks = true);
```

You can also setup the [ConnectionFactory](https://rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.ConnectionFactory.html) using the `Action<ConnectionFactory> configureConnectionFactory` delegate parameter of the `AddRabbitMQClient` method. For example to set the client provided name for connections:

```csharp
builder.AddRabbitMQClient("messaging", configureConnectionFactory: factory => factory.ClientProvidedName = "MyApp");
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.RabbitMQ` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.RabbitMQ
```

Then, in the _AppHost.cs_ file of `AppHost`, register a RabbitMQ server and consume the connection using the following methods:

```csharp
var messaging = builder.AddRabbitMQ("messaging");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(messaging);
```

The `WithReference` method configures a connection in the `MyService` project named `messaging`. In the _Program.cs_ file of `MyService`, the RabbitMQ connection can be consumed using:

```csharp
builder.AddRabbitMQClient("messaging");
```

## Additional documentation

* https://rabbitmq.github.io/rabbitmq-dotnet-client/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
